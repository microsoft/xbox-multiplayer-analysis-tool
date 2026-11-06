// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Cli;

namespace XMAT.Tests.Cli
{
    // --form, --form-string and --form-type as xmat parses them (Slice I4, C1): command-line order across the options, a
    // literal '@', media types by part number, the commands that take them, and every usage error, whose message repeats
    // nothing that was typed. Nothing is read when parsing.
    public sealed class CliFormCommandLineTests
    {
        private const string Canary = "XMATFORMCANARY44";

        [Fact]
        public void Forms_KeepCommandLineOrderAcrossOptions_AndTakeTypesByPartNumber()
        {
            string file = Path.Join(Path.GetTempPath(), "folder", "upload.bin");

            CliParseResult result = CliCommandLine.Parse(new[]
            {
                "compose", "--url", "https://a.test/", "--out", "o",
                "--form", "a=1", "--form-string", "b=@literal", "--form", "c=@" + file, "--form", "d=", "--form-string", "e=x=y",
                "--form-type", "2=text/plain; charset=utf-8", "--form-type", "3=application/pdf",
            });

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(
                new[]
                {
                    new CliFormPart("a", "1", null, null),
                    new CliFormPart("b", "@literal", null, "text/plain; charset=utf-8"),
                    new CliFormPart("c", null, file, "application/pdf"),
                    new CliFormPart("d", "", null, null),
                    new CliFormPart("e", "x=y", null, null),
                },
                result.Request.Replay.Form);
            Assert.Equal("upload.bin", result.Request.Replay.Form[2].FileName);
            Assert.Null(result.Request.Replay.Form[0].FileName);
        }

        // Every command that takes --body-file takes a form; without one, there is none.
        [Theory]
        [InlineData("replay", "a.saz", "--request", "1")]
        [InlineData("compose", "--url", "https://a.test/")]
        [InlineData("save-request", "--url", "https://a.test/")]
        [InlineData("curl", "--url", "https://a.test/", "--include-sensitive")]
        public void Forms_ApplyWhereverABodyFileDoes(params string[] command)
        {
            string[] withForm = command.Concat(new[] { "--out", "o", "--form", "a=b" }).ToArray();

            Assert.Equal("a", Assert.Single(CliCommandLine.Parse(withForm).Request.Replay.Form).Name);
            Assert.Null(CliCommandLine.Parse(command.Concat(new[] { "--out", "o" }).ToArray()).Request.Replay.Form);
        }

        [Theory]
        [InlineData("cli.usage.invalid-value", "--form", "nokey")]
        [InlineData("cli.usage.invalid-value", "--form", "=value")]
        [InlineData("cli.usage.invalid-value", "--form-string", "nokey")]
        [InlineData("cli.usage.invalid-value", "--form", "na\u0001me=value")]
        [InlineData("cli.usage.invalid-value", "--form", "caf\u00e9=value")]
        [InlineData("cli.usage.invalid-value", "--form", "a=@")]
        [InlineData("cli.usage.invalid-value", "--form", "a=@folder\\")]
        [InlineData("cli.usage.missing-option", "--form-type", "1=text/plain")]
        [InlineData("cli.usage.invalid-value", "--form", "a=b", "--form-type", "0=text/plain")]
        [InlineData("cli.usage.invalid-value", "--form", "a=b", "--form-type", "2=text/plain")]
        [InlineData("cli.usage.invalid-value", "--form", "a=b", "--form-type", "1=text/plain", "--form-type", "1=text/html")]
        [InlineData("cli.usage.invalid-value", "--form", "a=b", "--form-type", "1=text")]
        [InlineData("cli.usage.invalid-value", "--form", "a=b", "--form-type", "x=text/plain")]
        [InlineData("cli.usage.invalid-value", "--form", "a=b", "--form-type", "+1=text/plain")]
        [InlineData("cli.usage.invalid-value", "--form", "a=b", "--form-type", "1=text/plain\r\nX: y")]
        [InlineData("cli.usage.conflicting-options", "--form", "a=b", "--body-file", "b.bin")]
        [InlineData("cli.usage.conflicting-options", "--form-string", "a=b", "--header", "content-type: text/plain")]
        [InlineData("cli.usage.conflicting-options", "--form", "a=b", "--remove-header", "Content-Type")]
        public void Forms_RefuseWhatTheyCannotTake(string code, params string[] options)
        {
            CliParseResult result = CliCommandLine.Parse(
                new[] { "replay", "a.saz", "--request", "1", "--out", "o" }.Concat(options).ToArray());

            Assert.Equal(code, result.Error?.Code);
        }

        [Fact]
        public void Forms_RefuseAnEmptyBodyAndMoreThan128Parts()
        {
            string[] tooMany = Enumerable.Range(0, 129).SelectMany(index => new[] { "--form", $"p{index}=v" }).ToArray();

            Assert.Equal(
                "cli.usage.conflicting-options",
                CliCommandLine.Parse(new[] { "compose", "--request-file", "r.json", "--out", "o", "--form", "a=b", "--empty-body" })
                    .Error?.Code);
            Assert.Equal(
                "cli.usage.invalid-value",
                CliCommandLine.Parse(new[] { "compose", "--url", "https://a.test/", "--out", "o" }.Concat(tooMany).ToArray()).Error?.Code);
            Assert.Equal(
                128,
                CliCommandLine.Parse(new[] { "compose", "--url", "https://a.test/", "--out", "o" }.Concat(tooMany[..256]).ToArray())
                    .Request.Replay.Form.Count);
        }

        // A part number is a number, whatever its spelling: leading zeros select the same part, and one too large for any
        // form is refused.
        [Fact]
        public void FormTypes_TakeAnySpellingOfAPartNumber()
        {
            CliParseResult zeros = CliCommandLine.Parse(new[]
            {
                "compose", "--url", "https://a.test/", "--out", "o", "--form", "a=b", "--form-type", "0001=text/plain",
            });
            CliParseResult huge = CliCommandLine.Parse(new[]
            {
                "compose", "--url", "https://a.test/", "--out", "o", "--form", "a=b", "--form-type", "99999999999=text/plain",
            });

            Assert.Equal("text/plain", Assert.Single(zeros.Request.Replay.Form).MediaType);
            Assert.Equal("cli.usage.invalid-value", huge.Error?.Code);
        }

        // A refusal names options, never what was typed: not a name, value, path or type.
        [Theory]
        [InlineData("--form", Canary)]
        [InlineData("--form", "a\u0001" + Canary + "=v")]
        [InlineData("--form", "a=@" + Canary + "\\")]
        [InlineData("--form-type", "1=" + Canary)]
        public void Forms_RefuseWithoutRepeatingWhatWasTyped(string option, string value)
        {
            CliParseResult result = CliCommandLine.Parse(new[]
            {
                "compose", "--url", "https://a.test/", "--out", "o", "--form", "x=" + Canary, option, value,
            });

            Assert.False(result.Succeeded);
            Assert.DoesNotContain(Canary, result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
