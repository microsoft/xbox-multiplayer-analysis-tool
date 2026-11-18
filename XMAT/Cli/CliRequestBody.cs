// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations.Replay;

namespace XMAT.Cli
{
    // The body that replay, compose, save-request and curl make from their command line, before any output is checked or
    // reserved: the bytes of --body-file, or the multipart form of --form, --form-string and --form-type. Paths and contents
    // never reach the output; a failure is exit code 3 with a fixed message, and cancellation throws
    // OperationCanceledException, which each command reports as it always has.
    internal sealed class CliRequestBody
    {
        private CliRequestBody(byte[] body, MultipartFormBody form)
        {
            Body = body;
            Form = form;
        }

        internal static CliRequestBody None { get; } = new(null, null);

        // The --body-file bytes, or null.
        internal byte[] Body { get; }

        // The built form, or null.
        internal MultipartFormBody Form { get; }

        internal static Task<(CliRequestBody Body, CliFailure Failure)> PrepareAsync(
            CliReplay replay, CancellationToken cancellationToken) =>
            PrepareAsync(replay, ReplayBodyFile.ReadAsync, cancellationToken);

        // readFile reads one file within a budget: ReplayBodyFile.ReadAsync, but in tests. Each file is read once.
        internal static async Task<(CliRequestBody Body, CliFailure Failure)> PrepareAsync(
            CliReplay replay,
            Func<string, long, CancellationToken, Task<ReplayBodyRead>> readFile,
            CancellationToken cancellationToken)
        {
            if (replay.Form != null)
            {
                return await PrepareFormAsync(replay.Form, readFile, cancellationToken).ConfigureAwait(false);
            }

            if (replay.BodyFile == null)
            {
                return (None, null);
            }

            ReplayBodyRead read = await readFile(replay.BodyFile, ReplayBodyFile.MaxBytes, cancellationToken).ConfigureAwait(false);
            return read.Succeeded
                ? (new CliRequestBody(read.Body, null), null)
                : (null, new CliFailure(CliExitCodes.InvalidInput, read.Code, CliReplayCommand.BodyFileMessage(read.Code)));
        }

        // What --form failures say: never a name, value, path or type.
        internal static string FormMessage(string code) =>
            code switch
            {
                MultipartCodes.FileNotFound => "A --form file was not found.",
                MultipartCodes.FileUnreadable => "A --form file could not be read.",
                MultipartCodes.InvalidText => "A --form or --form-string value is not well-formed Unicode text.",
                MultipartCodes.TooLarge => "The form of --form and --form-string would be larger than 64 MiB.",
                _ => MultipartFormException.MessageFor(code),
            };

        // The text parts are made first. Then everything but the files' contents is reserved, framing of every part
        // included, before any file is opened; each file, in command-line order, is read with what is left of the 64 MiB,
        // so a file that does not fit is refused before anything of its size is allocated. Then the form is built.
        private static async Task<(CliRequestBody Body, CliFailure Failure)> PrepareFormAsync(
            IReadOnlyList<CliFormPart> form,
            Func<string, long, CancellationToken, Task<ReplayBodyRead>> readFile,
            CancellationToken cancellationToken)
        {
            var parts = new MultipartPart[form.Count];
            try
            {
                long reserved = MultipartForm.ClosingLength;
                for (int index = 0; index < form.Count; index++)
                {
                    CliFormPart part = form[index];
                    reserved += MultipartForm.PartFramingLength(part.IsFile, part.Name, part.FileName, part.MediaType);
                    if (!part.IsFile)
                    {
                        parts[index] = MultipartPart.Text(part.Name, part.Value, part.MediaType, cancellationToken);
                        reserved += parts[index].ContentLength;
                    }
                }

                if (reserved > MultipartForm.MaxBodyBytes)
                {
                    return (null, Failure(MultipartCodes.TooLarge));
                }

                long read = 0;
                for (int index = 0; index < form.Count; index++)
                {
                    CliFormPart part = form[index];
                    if (!part.IsFile)
                    {
                        continue;
                    }

                    ReplayBodyRead file = await readFile(
                        part.Path, MultipartForm.MaxBodyBytes - reserved - read, cancellationToken).ConfigureAwait(false);
                    if (!file.Succeeded)
                    {
                        return (null, Failure(file.Code switch
                        {
                            ReplayBodyFile.TooLargeCode => MultipartCodes.TooLarge,
                            ReplayBodyFile.NotFoundCode => MultipartCodes.FileNotFound,
                            _ => MultipartCodes.FileUnreadable,
                        }));
                    }

                    read += file.Body.LongLength;
                    parts[index] = MultipartPart.File(part.Name, part.FileName, file.Body, part.MediaType);
                }

                return (new CliRequestBody(null, MultipartForm.Build(parts, cancellationToken)), null);
            }
            catch (MultipartFormException ex)
            {
                return (null, Failure(ex.Code));
            }
        }

        private static CliFailure Failure(string code) => new(CliExitCodes.InvalidInput, code, FormMessage(code));
    }
}
