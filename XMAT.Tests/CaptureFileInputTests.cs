// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class CaptureFileInputTests
    {
        [Fact]
        public async Task ScriptInputBindsOriginalBytesAndUsesBomAwareDesktopDecoding()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "source.txt");
            const string text = "// \u03bb\U0001F600\r\nParams.Continue = false;\n";
            await File.WriteAllTextAsync(path, text, Encoding.Unicode);
            var bytes = await File.ReadAllBytesAsync(path);
            var inputs = new CaptureFileInputs();
            var descriptor = await inputs.RegisterAsync(path, 65536);
            var source = await inputs.ReadScriptAsync(descriptor.Id, 65536);
            Assert.Equal(text, source.Source);
            Assert.Equal(descriptor, source.File);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), descriptor.Sha256);
            Assert.Equal(bytes.Length, descriptor.Length);
            Assert.Equal("source.txt", descriptor.FileName);
            Assert.Equal(descriptor.Id, (await inputs.RegisterAsync(path, 65536)).Id);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }

        [Fact]
        public async Task ChangedOrReplacedInputsCannotReuseTheReviewedIdentity()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "source.txt");
            await File.WriteAllTextAsync(path, "original source");
            var inputs = new CaptureFileInputs();
            var original = await inputs.RegisterAsync(path, 65536);
            await File.WriteAllTextAsync(path, "modified source");
            Assert.Equal(CaptureFileFailure.InputChanged, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                inputs.ReadScriptAsync(original.Id, 65536))).Failure);
            var changed = await inputs.RegisterAsync(path, 65536);
            Assert.NotEqual(original.Id, changed.Id);
            var replacement = Path.Combine(directory.Path, "replacement.txt");
            await File.WriteAllTextAsync(replacement, "modified source");
            File.Move(replacement, path, overwrite: true);
            Assert.Equal(CaptureFileFailure.InputChanged, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                inputs.ValidateAsync(changed.Id, 65536))).Failure);
            Assert.NotEqual(changed.Id, (await inputs.RegisterAsync(path, 65536)).Id);
        }

        [Fact]
        public async Task CaptureInputsCopyOnlyTheReviewedBytesAndCannotBeDecodedAsScriptSources()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "capture.xmatcap");
            CaptureFileLoadTests.WriteCapture(path, [("loaded", CaptureType.WebProxy)]);
            var bytes = await File.ReadAllBytesAsync(path);
            var inputs = new CaptureFileInputs();
            var input = await inputs.RegisterAsync(path, 65536);
            using var output = new MemoryStream();
            Assert.Equal(input, await inputs.CopyCaptureAsync(input.Id, output, 65536));
            Assert.Equal(bytes, output.ToArray());
            Assert.Equal(CaptureFileFailure.InvalidPath, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                inputs.ReadScriptAsync(input.Id, 65536))).Failure);
            Assert.Equal(QueryError.ResultLimit, (await Assert.ThrowsAsync<QueryException>(() =>
                inputs.CopyCaptureAsync(input.Id, output, 1))).Error);
            var scriptPath = Path.Combine(directory.Path, "script.txt");
            await File.WriteAllTextAsync(scriptPath, "// source");
            var script = await inputs.RegisterAsync(scriptPath, 65536);
            Assert.Equal(CaptureFileFailure.InvalidPath, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                inputs.CopyCaptureAsync(script.Id, output, 65536))).Failure);
            await File.WriteAllBytesAsync(path, [0, 0xff, 0x80, 1, 2, 0, 4]);
            Assert.Equal(CaptureFileFailure.InputChanged, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                inputs.CopyCaptureAsync(input.Id, output, 65536))).Failure);
        }

        [Fact]
        public void InputHandlePinsTheReviewedFileAgainstWritesAndReplacement()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "source.txt");
            var replacement = Path.Combine(directory.Path, "replacement.txt");
            File.WriteAllText(path, "reviewed");
            File.WriteAllText(replacement, "changed");
            using (var folder = new WindowsExportFolder(directory.Path))
            using (var file = folder.OpenInput("source.txt", out _))
            {
                Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
                var blocked = Record.Exception(() => File.Move(replacement, path, overwrite: true));
                Assert.True(blocked is IOException or UnauthorizedAccessException);
                using var reader = new StreamReader(file, leaveOpen: true);
                Assert.Equal("reviewed", reader.ReadToEnd());
            }
            File.Move(replacement, path, overwrite: true);
            Assert.Equal("changed", File.ReadAllText(path));
        }

        [Fact]
        public async Task BothOriginalAndDecodedInputsMustFitTheApprovedBudget()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "source.txt");
            await File.WriteAllTextAsync(path, new string('x', 5000));
            var inputs = new CaptureFileInputs();
            Assert.Equal(QueryError.ResultLimit, (await Assert.ThrowsAsync<QueryException>(() =>
                inputs.RegisterAsync(path, 100))).Error);
            Assert.Empty(inputs.GetInputs());
            await File.WriteAllBytesAsync(path, [0xff]);
            var input = await inputs.RegisterAsync(path, 1);
            Assert.Equal(QueryError.ResultLimit, (await Assert.ThrowsAsync<QueryException>(() =>
                inputs.ReadScriptAsync(input.Id, 1))).Error);
            Assert.Equal(new byte[] { 0xff }, await File.ReadAllBytesAsync(path));
        }

        [Fact]
        public async Task ReplacedParentDirectoryCannotReuseAnApprovedInput()
        {
            using var directory = new CaptureFileExportTests.DirectoryFixture();
            var path = Path.Combine(directory.Path, "source.txt");
            await File.WriteAllTextAsync(path, "// reviewed");
            var inputs = new CaptureFileInputs();
            var input = await inputs.RegisterAsync(path, 65536);
            var previous = directory.Path + ".previous";
            Directory.Move(directory.Path, previous);
            Directory.CreateDirectory(directory.Path);
            try
            {
                await File.WriteAllTextAsync(path, "// reviewed");
                Assert.Equal(CaptureFileFailure.InputChanged, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                    inputs.ReadScriptAsync(input.Id, 65536))).Failure);
                Assert.NotEqual(input.Id, (await inputs.RegisterAsync(path, 65536)).Id);
            }
            finally { Directory.Delete(previous, recursive: true); }
        }

        [Theory]
        [InlineData(@"\\server\share\script.txt")]
        [InlineData(@"\\?\C:\script.txt")]
        [InlineData(@"C:script.txt")]
        [InlineData(@"relative\script.txt")]
        [InlineData(@"C:\script.txt:stream")]
        [InlineData(@"C:\capture.zip")]
        public async Task NonLocalOrUntypedPathsCannotBeRegistered(string path)
        {
            Assert.Equal(CaptureFileFailure.InvalidPath, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                new CaptureFileInputs().RegisterAsync(path, 65536))).Failure);
        }
    }
}
