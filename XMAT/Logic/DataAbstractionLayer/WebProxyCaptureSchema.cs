// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.DataAbstractionLayer
{
    // What is known about whether a session's stored header lines are all that its messages had, as the
    // HeadersUninspectable column and the x-xmat-headers-uninspectable SAZ flag store it. A sanitized export inspects an
    // unknown history as it is and reports it, and leaves an uninspectable session's pair out.
    internal enum HeaderProvenance
    {
        // Nothing recorded it, as in a capture or archive written before XMAT did: earlier processing may have dropped
        // header lines without a trace.
        Unknown = -1,

        // XMAT recorded no loss when it stored the headers. This doesn't prove that nothing was lost before that.
        NoKnownLoss = 0,

        // An import left header lines out or couldn't trust how they were encoded.
        Uninspectable = 1,
    }

    internal static class WebProxyCaptureSchema
    {
        internal const string ClientProcessName = "ClientProcessName";
        internal const string ClientProcessId = "ClientProcessId";
        internal const string ClientProcessStartTime = "ClientProcessStartTime";
        internal const string ClientProcessStatus = "ClientProcessStatus";
        internal const string ResponseTrailers = "ResponseTrailers";
        internal const string ResponseBodyTruncatedAt = "ResponseBodyTruncatedAt";
        internal const string HeadersUninspectable = "HeadersUninspectable";

        internal static IReadOnlyList<IField> Fields { get; } =
            Array.AsReadOnly<IField>(
                new IField[]
                {
                    new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                    new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                    new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP),
                    new Field<string>(ClientProcessName),
                    new Field<long>(ClientProcessId),
                    new Field<string>(ClientProcessStartTime),
                    new Field<string>(ClientProcessStatus),
                    new Field<string>(ResponseTrailers),
                    new Field<long>(ResponseBodyTruncatedAt),
                    new Field<long>(HeadersUninspectable),
                });

        internal static IField[] CreateFields() => Fields.ToArray();

        // Captures saved before these optional columns existed have no trailers and complete response bodies.
        internal static string ReadResponseTrailers(IDataRecord record) =>
            record is IOptionalTypedDataRecord optional && optional.TryReadString(ResponseTrailers, out string trailers)
                ? trailers
                : string.Empty;

        internal static long ReadResponseBodyTruncatedAt(IDataRecord record) =>
            record is IOptionalTypedDataRecord optional &&
                optional.TryReadInt64(ResponseBodyTruncatedAt, out long truncatedAt) &&
                truncatedAt > 0
                ? truncatedAt
                : 0;

        // The stored HeadersUninspectable value: -1, 0 or 1 (HeaderProvenance). A capture saved before the column existed,
        // or a row without the field, has an unknown history. NULL or any other stored value can't be trusted, so it
        // reads as uninspectable rather than unknown.
        internal static HeaderProvenance ReadHeaderProvenance(IDataRecord record) =>
            record is IRawDataRecord raw && raw.TryReadRaw(HeadersUninspectable, out object value)
                ? ParseHeaderProvenance(value)
                : HeaderProvenance.Unknown;

        // The same for a row's stored values by column name, as an export reads them.
        internal static HeaderProvenance HeaderProvenanceOf(IReadOnlyDictionary<string, object> values) =>
            values != null && values.TryGetValue(HeadersUninspectable, out object value)
                ? ParseHeaderProvenance(value)
                : HeaderProvenance.Unknown;

        // The more cautious of two states, so a copy or an import never loses what either knew: uninspectable over unknown
        // over no known loss.
        internal static HeaderProvenance Combine(HeaderProvenance first, HeaderProvenance second) =>
            first == HeaderProvenance.Uninspectable || second == HeaderProvenance.Uninspectable
                ? HeaderProvenance.Uninspectable
                : first == HeaderProvenance.Unknown || second == HeaderProvenance.Unknown
                    ? HeaderProvenance.Unknown
                    : HeaderProvenance.NoKnownLoss;

        private static HeaderProvenance ParseHeaderProvenance(object value) => value switch
        {
            -1L => HeaderProvenance.Unknown,
            0L => HeaderProvenance.NoKnownLoss,
            _ => HeaderProvenance.Uninspectable,
        };

        internal static object[] Project(IReadOnlyDictionary<string, object> values, ClientProcessInfo process)
        {
            ArgumentNullException.ThrowIfNull(values);
            Dictionary<string, object> projected = values.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);
            foreach (IFieldValue field in ClientProcessRecordCodec.ToFields(process))
            {
                projected[field.Name] = field.Value;
            }
            // A row without the header history keeps it unknown, never "no known loss".
            return Fields.Select(field => field.Name == HeadersUninspectable
                ? (long)HeaderProvenanceOf(projected)
                : Normalize(field, projected.TryGetValue(field.Name, out object value) ? value : null)).ToArray();
        }

        private static object Normalize(IField field, object value)
        {
            if (value != null)
            {
                return value;
            }
            return field.ValueType == typeof(string) ? string.Empty : 0L;
        }
    }
}
