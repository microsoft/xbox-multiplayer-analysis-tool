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
    internal static class WebProxyCaptureSchema
    {
        internal const string ClientProcessName = "ClientProcessName";
        internal const string ClientProcessId = "ClientProcessId";
        internal const string ClientProcessStartTime = "ClientProcessStartTime";
        internal const string ClientProcessStatus = "ClientProcessStatus";

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
                });

        internal static IField[] CreateFields() => Fields.ToArray();

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
            return Fields.Select(field => Normalize(field, projected.TryGetValue(field.Name, out object value) ? value :
                null)).ToArray();
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
