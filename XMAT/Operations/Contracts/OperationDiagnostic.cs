// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Operations
{
    internal enum OperationDiagnosticSeverity
    {
        Info,
        Warning,
        Error,
    }

    // Code is a stable machine id such as "analysis.input.not-found". Message is fixed, data-free text: never raw
    // exception text, captured content, hosts, URLs, identifiers or paths derived from captured data.
    internal sealed record OperationDiagnostic(string Code, OperationDiagnosticSeverity Severity, string Message);
}
