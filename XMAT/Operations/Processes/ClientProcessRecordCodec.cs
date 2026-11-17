// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Operations.Processes
{
    internal static class ClientProcessRecordCodec
    {
        internal static ClientProcessInfo Read(IDataRecord record)
        {
            if (record is not IOptionalTypedDataRecord optional)
            {
                return ClientProcessInfo.NotRecorded;
            }
            _ = optional.TryReadString(WebProxyCaptureSchema.ClientProcessName, out string name);
            _ = optional.TryReadInt64(WebProxyCaptureSchema.ClientProcessId, out long processId);
            _ = optional.TryReadString(WebProxyCaptureSchema.ClientProcessStartTime, out string startTime);
            _ = optional.TryReadString(WebProxyCaptureSchema.ClientProcessStatus, out string status);
            return FromValues(name ?? string.Empty, processId, startTime ?? string.Empty, status ?? string.Empty);
        }

        internal static ClientProcessInfo FromValues(string name, long pid, string startUtc, string statusToken)
        {
            if (!TryStatus(statusToken, out ClientProcessStatus status) ||
                pid < 0 ||
                pid > int.MaxValue ||
                !TryNormalizeName(name, out string basename) ||
                !TryStartTime(startUtc, out DateTimeOffset? startTime))
            {
                return ClientProcessInfo.NotRecorded;
            }
            if (status == ClientProcessStatus.NotRecorded)
            {
                return ClientProcessInfo.NotRecorded;
            }
            if (status is ClientProcessStatus.Remote or ClientProcessStatus.Unresolved)
            {
                return new ClientProcessInfo(string.Empty, 0, null, status);
            }
            if (string.IsNullOrEmpty(basename) && pid == 0)
            {
                return ClientProcessInfo.NotRecorded;
            }
            return new ClientProcessInfo(basename, (int)pid, startTime, status);
        }

        internal static IReadOnlyList<IFieldValue> ToFields(ClientProcessInfo info)
        {
            ClientProcessInfo value = info ?? ClientProcessInfo.NotRecorded;
            ClientProcessInfo normalized = FromValues(
                value.Name,
                value.ProcessId,
                value.StartTimeUtc?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
                StatusToken(value.Status));
            return Array.AsReadOnly<IFieldValue>(
                new IFieldValue[]
                {
                    new FieldValue<string>(WebProxyCaptureSchema.ClientProcessName, normalized.Name ?? string.Empty),
                    new FieldValue<long>(WebProxyCaptureSchema.ClientProcessId, normalized.ProcessId),
                    new FieldValue<string>(
                        WebProxyCaptureSchema.ClientProcessStartTime,
                        normalized.StartTimeUtc?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ??
                            string.Empty),
                    new FieldValue<string>(WebProxyCaptureSchema.ClientProcessStatus, StatusToken(normalized.Status)),
                });
        }

        internal static string StatusToken(
            ClientProcessStatus status) =>
            status switch
            {
                ClientProcessStatus.Local => "local",
                ClientProcessStatus.Imported => "imported",
                ClientProcessStatus.Remote => "remote",
                ClientProcessStatus.Unresolved => "unresolved",
                _ => string.Empty,
            };

        private static bool TryStatus(string value, out ClientProcessStatus status)
        {
            status =
                value switch
                {
                    "" => ClientProcessStatus.NotRecorded,
                    "local" => ClientProcessStatus.Local,
                    "imported" => ClientProcessStatus.Imported,
                    "remote" => ClientProcessStatus.Remote,
                    "unresolved" => ClientProcessStatus.Unresolved,
                    _ => (ClientProcessStatus)(-1),
                };
            return (int)status >= 0;
        }

        internal static bool TryNormalizeName(string value, out string basename)
        {
            basename = value ?? string.Empty;
            int separator = basename.LastIndexOfAny(
                    ['\\', '/']);
            if (separator >= 0)
            {
                basename =
                    basename[(separator + 1)..];
            }
            if (basename.Length > 260 ||
                basename.Any(character => char.IsControl(character) || character is '\\' or '/' or ':' or '"' or '<' or
                    '>' or '|'))
            {
                basename = string.Empty;
                return false;
            }
            return true;
        }

        private static bool TryStartTime(string value, out DateTimeOffset? startTime)
        {
            startTime = null;
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }
            if (!DateTimeOffset.TryParseExact(
                    value,
                    "o",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTimeOffset parsed))
            {
                return false;
            }
            startTime = parsed.ToUniversalTime();
            return true;
        }
    }
}
