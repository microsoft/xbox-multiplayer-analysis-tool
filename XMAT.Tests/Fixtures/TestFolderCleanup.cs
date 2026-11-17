// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests.Fixtures
{
    // Removes a folder that a test made, with everything in it, or one file. Something outside the test, such as a scanner,
    // can hold a file that the test has just written for a moment (observed on a fresh .saz), so a delete that fails with a
    // sharing or access error is tried again, 20 times in all, 100 ms apart. One that still fails then throws, so a handle
    // that a test leaked still fails that test. prepare, when given, runs before every attempt with the attempt's number,
    // from 1.
    internal static class TestFolderCleanup
    {
        internal const int DefaultAttempts = 20;

        internal static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(100);

        internal static void Delete(string path, Action<int> prepare = null) =>
            Delete(path, prepare, DefaultAttempts, DefaultPause);

        internal static void Delete(string path, Action<int> prepare, int attempts, TimeSpan pause) =>
            Retry(path, Directory.Exists, folder => Directory.Delete(folder, recursive: true), prepare, attempts, pause);

        internal static void DeleteFile(string path, Action<int> prepare = null) =>
            DeleteFile(path, prepare, DefaultAttempts, DefaultPause);

        internal static void DeleteFile(string path, Action<int> prepare, int attempts, TimeSpan pause) =>
            Retry(path, File.Exists, File.Delete, prepare, attempts, pause);

        private static void Retry(
            string path, Func<string, bool> exists, Action<string> delete, Action<int> prepare, int attempts, TimeSpan pause)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempts);
            for (int attempt = 1; exists(path); attempt++)
            {
                try
                {
                    prepare?.Invoke(attempt);
                    delete(path);
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < attempts)
                {
                    Thread.Sleep(pause);
                }
            }
        }
    }
}
