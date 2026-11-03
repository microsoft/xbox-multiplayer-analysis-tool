// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum CaptureInputKind { ScriptSource, CaptureDatabase, FiddlerSaz, NetworkTraceEtl }
    public sealed record InputFileDescriptor(
        Guid Id, CaptureInputKind Kind, string FileName, long Length, string Sha256);
    public sealed record ScriptFileInput(InputFileDescriptor File, string Source);

    public interface ICaptureFileInputs
    {
        Task<InputFileDescriptor> RegisterAsync(
            string path, long maximumBytes, CancellationToken cancellationToken = default);
        IReadOnlyList<InputFileDescriptor> GetInputs();
        Task ValidateAsync(Guid inputId, long maximumBytes, CancellationToken cancellationToken = default);
        Task<ScriptFileInput> ReadScriptAsync(
            Guid inputId, long maximumBytes, CancellationToken cancellationToken = default);
        Task<InputFileDescriptor> CopyCaptureAsync(
            Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default);
        Task<InputFileDescriptor> CopySazAsync(
            Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default);
        Task<InputFileDescriptor> CopyEtlAsync(
            Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
