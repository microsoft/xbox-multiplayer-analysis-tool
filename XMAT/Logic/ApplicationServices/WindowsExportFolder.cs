// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class WindowsExportFolder : IDisposable
    {
        private readonly List<SafeFileHandle> _directories = new();
        internal string Path { get; }
        internal FileIdentity Identity { get; private set; }

        internal WindowsExportFolder(string path)
        {
            Path = Normalize(path);
            try
            {
                var root = System.IO.Path.GetPathRoot(Path);
                var drive = new DriveInfo(root);
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) ||
                    drive.DriveFormat is not ("NTFS" or "ReFS"))
                    throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                Pin(root);
                var current = root.TrimEnd('\\');
                foreach (var component in Path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (component.EndsWith('.') || component.EndsWith(' '))
                        throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                    current += "\\" + component;
                    Pin(current);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('\\') || path.Contains('/') ||
                !System.IO.Path.IsPathFullyQualified(path) || path.Length > 240)
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd('\\');
        }

        private void Pin(string path)
        {
            // Attribute-only handles do not enforce directory rename sharing.
            var handle = CreateFile(path, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new CaptureFileException(CaptureFileFailure.Unavailable);
            }
            _directories.Add(handle);
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & 0x10) == 0 || (info.Attributes & 0x400) != 0)
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var name = new StringBuilder(512);
            var count = GetFinalPathNameByHandle(handle, name, name.Capacity, 0);
            var finalPath = name.ToString().Replace(@"\\?\", string.Empty, StringComparison.Ordinal).TrimEnd('\\');
            if (count == 0 || count >= name.Capacity ||
                !string.Equals(finalPath, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            Identity = new(info.Volume, info.IndexHigh, info.IndexLow);
        }

        internal string Destination(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 160 ||
                fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                fileName.EndsWith('.') || fileName.EndsWith(' ') ||
                fileName.Any(char.IsControl) || fileName is "." or "..")
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var stem = fileName.Split('.')[0].TrimEnd(' ');
            var upper = stem.ToUpperInvariant();
            if (upper is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) ||
                    upper.StartsWith("LPT", StringComparison.Ordinal)) &&
                    "123456789\u00b9\u00b2\u00b3".Contains(upper[3])))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            return System.IO.Path.Combine(Path, fileName);
        }

        internal static void DemandNew(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new CaptureFileException(CaptureFileFailure.DestinationExists);
        }

        internal FileStream CreateTemporary()
        {
            var path = System.IO.Path.Combine(Path, $".xmat-export-{Guid.NewGuid():N}.tmp");
            var handle = CreateFile(path, 0x40010080, 0, IntPtr.Zero, 1, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new CaptureFileException(CaptureFileFailure.Unavailable);
            }
            return new FileStream(handle, FileAccess.Write, 4096, isAsync: true);
        }

        internal OwnedDirectory CreateTemporaryDirectory()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var path = System.IO.Path.Combine(Path, $".xmat-{Guid.NewGuid():N}"[..14]);
                if (!CreateDirectoryNative(path, IntPtr.Zero))
                {
                    if (Marshal.GetLastWin32Error() is 80 or 183)
                        continue;
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                }
                var handle = CreateFile(path, 0x10080, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                }
                try
                {
                    if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (0x10 | 0x400)) != 0x10)
                        throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                    var name = new StringBuilder(512);
                    var count = GetFinalPathNameByHandle(handle, name, name.Capacity, 0);
                    if (count == 0 || count >= name.Capacity ||
                        !string.Equals(name.ToString().Replace(@"\\?\", string.Empty, StringComparison.Ordinal), path, StringComparison.OrdinalIgnoreCase))
                        throw new CaptureFileException(CaptureFileFailure.FolderChanged);
                    return new(this, handle, path);
                }
                catch { handle.Dispose(); throw; }
            }
            throw new CaptureFileException(CaptureFileFailure.Unavailable);
        }

        internal FileStream OpenInput(string fileName, out FileIdentity identity)
        {
            var path = Destination(fileName);
            // Pin the reviewed file against writes/replacement and do not follow its reparse point.
            var handle = CreateFile(path, 0x80000080, 1, IntPtr.Zero, 3, 0x40200000, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid)
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                if (!GetFileInformationByHandle(handle, out var info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & (0x10 | 0x400)) != 0)
                    throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                var name = new StringBuilder(512);
                var count = GetFinalPathNameByHandle(handle, name, name.Capacity, 0);
                if (count == 0 || count >= name.Capacity ||
                    !string.Equals(name.ToString().Replace(@"\\?\", string.Empty, StringComparison.Ordinal),
                        path, StringComparison.OrdinalIgnoreCase))
                    throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                identity = new(info.Volume, info.IndexHigh, info.IndexLow);
                return new FileStream(handle, FileAccess.Read, 4096, isAsync: true);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal static void Publish(FileStream file, string destination)
            => PublishHandle(file.SafeFileHandle, destination);

        private static void PublishHandle(SafeFileHandle handle, string destination)
        {
            // FILE_RENAME_INFO has an aligned handle at 8 and its UTF-16 name at 20 on x64.
            var name = Encoding.Unicode.GetBytes(destination + "\0");
            var buffer = Marshal.AllocHGlobal(20 + name.Length);
            try
            {
                Marshal.Copy(new byte[20 + name.Length], 0, buffer, 20 + name.Length);
                Marshal.WriteInt32(buffer, 16, name.Length - 2);
                Marshal.Copy(name, 0, buffer + 20, name.Length);
                if (!SetFileInformationByHandle(handle, 3, buffer, 20 + name.Length))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new CaptureFileException(error is 80 or 183
                        ? CaptureFileFailure.DestinationExists : CaptureFileFailure.Unavailable);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static void DeleteTemporary(FileStream file)
            => DeleteHandle(file.SafeFileHandle);

        private static void DeleteHandle(SafeFileHandle handle)
        {
            if (!TryDeleteHandle(handle, out _))
                throw new CaptureFileException(CaptureFileFailure.Unavailable);
        }

        private static bool TryDeleteHandle(SafeFileHandle handle, out int error)
        {
            var buffer = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buffer, 1);
                var deleted = SetFileInformationByHandle(handle, 4, buffer, 4);
                error = deleted ? 0 : Marshal.GetLastWin32Error();
                return deleted;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            for (var index = _directories.Count - 1; index >= 0; index--)
                _directories[index].Dispose();
            _directories.Clear();
        }

        internal readonly record struct FileIdentity(uint Volume, uint High, uint Low);

        internal sealed class OwnedDirectory(WindowsExportFolder parent, SafeFileHandle handle, string path) : IDisposable
        {
            internal string Path => path;
            internal bool Published { get; private set; }
            private bool _publicationUncertain;
            private string _destination;

            internal FileStream CreateFile(string fileName)
            {
                parent.Destination(fileName);
                var file = WindowsExportFolder.CreateFile(System.IO.Path.Combine(path, fileName),
                    0xC0010080, 0, IntPtr.Zero, 1, 0x40200000, IntPtr.Zero);
                if (file.IsInvalid)
                {
                    file.Dispose();
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                }
                return new(file, FileAccess.ReadWrite, 4096, isAsync: true);
            }

            internal void Publish(string directoryName)
            {
                var destination = parent.Destination(directoryName);
                _destination = destination;
                DemandNew(destination);
                try
                {
                    PublishHandle(handle, destination);
                    Published = true;
                }
                catch (CaptureFileException exception)
                {
                    var current = FinalName();
                    if (string.Equals(current, destination, StringComparison.OrdinalIgnoreCase))
                    {
                        Published = true;
                        return;
                    }
                    if (string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                        throw;
                    _publicationUncertain = true;
                    throw new AnalysisReportExportException(AnalysisReportExportFailureKind.PublishFailed,
                        "The report publication could not be confirmed.", exception, AnalysisReportPublicationState.Unknown, destination);
                }
            }

            internal void DeleteUnpublished()
            {
                if (Published)
                    return;
                if (_publicationUncertain)
                {
                    var current = FinalName();
                    if (string.Equals(current, _destination, StringComparison.OrdinalIgnoreCase))
                    {
                        Published = true;
                        return;
                    }
                    if (!string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                        throw new CaptureFileException(CaptureFileFailure.Unavailable);
                    _publicationUncertain = false;
                }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        DeleteUnpublishedEntries();
                        return;
                    }
                    catch (IOException exception) when (attempt < 12 && CleanupConflict(exception))
                    {
                        // Keep the original directory pinned while overlapped closes or scanners drain.
                        Thread.Sleep(Math.Min(20 << Math.Min(attempt, 3), 160));
                    }
                }
            }

            private void DeleteUnpublishedEntries()
            {
                foreach (var fileName in Directory.EnumerateFiles(path))
                {
                    using var file = WindowsExportFolder.CreateFile(fileName, 0x10080, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (file.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error is 2 or 3)
                            continue;
                        throw new CleanupException(error);
                    }
                    if (!TryDeleteHandle(file, out var failure))
                        throw new CleanupException(failure);
                }
                if (Directory.EnumerateDirectories(path).Any())
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                if (!TryDeleteHandle(handle, out var directoryFailure))
                    throw new CleanupException(directoryFailure);
            }

            public void Dispose() => handle.Dispose();

            private string FinalName()
            {
                var name = new StringBuilder(512);
                var count = GetFinalPathNameByHandle(handle, name, name.Capacity, 0);
                return count > 0 && count < name.Capacity
                    ? name.ToString().Replace(@"\\?\", string.Empty, StringComparison.Ordinal) : null;
            }

            private static bool CleanupConflict(IOException exception) =>
                (exception is CleanupException cleanup ? cleanup.Error : exception.HResult & 0xFFFF) is 5 or 32 or 33 or 145;

            private sealed class CleanupException(int error) : IOException("An owned report export entry could not be removed.")
            {
                internal int Error { get; } = error;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal uint CreationLow;
            internal uint CreationHigh;
            internal uint AccessLow;
            internal uint AccessHigh;
            internal uint WriteLow;
            internal uint WriteHigh;
            internal uint Volume;
            internal uint SizeHigh;
            internal uint SizeLow;
            internal uint Links;
            internal uint IndexHigh;
            internal uint IndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryNative(string path, IntPtr security);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, int length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr info, int length);
    }
}
