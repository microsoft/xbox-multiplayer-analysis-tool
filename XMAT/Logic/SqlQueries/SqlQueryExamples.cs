// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.SqlQueries
{
    internal sealed record SqlQueryExample(string Title, string Sql);

    internal static class SqlQueryExamples
    {
        internal static string FieldHelp(string table, string column)
        {
            string key = null;
            if (table.Equals(WebServiceCaptureMethod.TableName, StringComparison.OrdinalIgnoreCase))
            {
                if (column.Equals(WebServiceCaptureMethod.FieldKey_RequestHost, StringComparison.OrdinalIgnoreCase))
                    key = "SQL_HELP_REQUEST_HOST";
                else if (column.Equals(WebServiceCaptureMethod.FieldKey_RequestStatus, StringComparison.OrdinalIgnoreCase))
                    key = "SQL_HELP_REQUEST_STATUS";
                else if (column.Equals(WebServiceCaptureMethod.FieldKey_RequestTimestamp, StringComparison.OrdinalIgnoreCase) ||
                    column.Equals(WebServiceCaptureMethod.FieldKey_ResponseTimestamp, StringComparison.OrdinalIgnoreCase))
                    key = "SQL_HELP_TIMESTAMP";
                else if (column.Equals(WebServiceCaptureMethod.FieldKey_RequestBody, StringComparison.OrdinalIgnoreCase) ||
                    column.Equals(WebServiceCaptureMethod.FieldKey_ResponseBody, StringComparison.OrdinalIgnoreCase))
                    key = "SQL_HELP_BASE64";
            }
            else if (table.Equals(NetworkTraceCaptureMethod.TableName, StringComparison.OrdinalIgnoreCase))
            {
                if (column.Equals(NetworkTraceCaptureMethod.FieldKey_Timestamp, StringComparison.OrdinalIgnoreCase))
                    key = "SQL_HELP_TIMESTAMP";
                else if (column.Equals(NetworkTraceCaptureMethod.FieldKey_Payload, StringComparison.OrdinalIgnoreCase))
                    key = "SQL_HELP_BASE64";
            }
            return key == null ? string.Empty : "\n" + Localization.GetLocalizedString(key);
        }

        internal static IReadOnlyList<SqlQueryExample> Create(IReadOnlyList<QueryTable> schema)
        {
            var examples = new List<SqlQueryExample>();
            Add("SQL_EXAMPLE_HTTP_LATEST", WebServiceCaptureMethod.TableName,
                ["id", WebServiceCaptureMethod.FieldKey_RequestNumber, WebServiceCaptureMethod.FieldKey_RequestMethod,
                    WebServiceCaptureMethod.FieldKey_RequestHost, WebServiceCaptureMethod.FieldKey_RequestPath,
                    WebServiceCaptureMethod.FieldKey_RequestStatus, WebServiceCaptureMethod.FieldKey_RequestTimestamp],
                (table, columns) => $"SELECT {string.Join(", ", columns)}\r\nFROM {table}\r\nORDER BY {columns[0]} DESC\r\nLIMIT 1000;");
            foreach (var field in new[]
            {
                (WebServiceCaptureMethod.FieldKey_RequestHost, "SQL_EXAMPLE_HTTP_HOSTS"),
                (WebServiceCaptureMethod.FieldKey_RequestMethod, "SQL_EXAMPLE_HTTP_METHODS"),
                (WebServiceCaptureMethod.FieldKey_DeviceName, "SQL_EXAMPLE_HTTP_DEVICES")
            })
            {
                Add(field.Item2, WebServiceCaptureMethod.TableName, [field.Item1],
                    (table, columns) => $"SELECT {columns[0]}, COUNT(*) AS \"CallCount\"\r\nFROM {table}\r\nGROUP BY {columns[0]}\r\nORDER BY \"CallCount\" DESC;");
            }
            Add("SQL_EXAMPLE_HTTP_ERRORS", WebServiceCaptureMethod.TableName,
                [WebServiceCaptureMethod.FieldKey_RequestStatus],
                (table, columns) => $"SELECT *\r\nFROM {table}\r\nWHERE CAST({columns[0]} AS INTEGER) >= 400\r\nLIMIT 1000;");
            Add("SQL_EXAMPLE_HTTP_SLOW", WebServiceCaptureMethod.TableName,
                [WebServiceCaptureMethod.FieldKey_RequestTimestamp, WebServiceCaptureMethod.FieldKey_ResponseTimestamp],
                (table, columns) => $"SELECT *, (julianday({columns[1]}) - julianday({columns[0]})) * 86400000.0 AS \"DurationMs\"\r\nFROM {table}\r\nWHERE {columns[1]} IS NOT NULL\r\nORDER BY \"DurationMs\" DESC\r\nLIMIT 1000;");
            foreach (var field in new[]
            {
                (NetworkTraceCaptureMethod.FieldKey_ProcessId, "SQL_EXAMPLE_PACKET_PROCESSES"),
                (NetworkTraceCaptureMethod.FieldKey_DeviceName, "SQL_EXAMPLE_PACKET_DEVICES")
            })
            {
                Add(field.Item2, NetworkTraceCaptureMethod.TableName, [field.Item1],
                    (table, columns) => $"SELECT {columns[0]}, COUNT(*) AS \"PacketCount\"\r\nFROM {table}\r\nGROUP BY {columns[0]}\r\nORDER BY \"PacketCount\" DESC;");
            }
            Add("SQL_EXAMPLE_CTE_JOIN", WebServiceCaptureMethod.TableName,
                ["id", WebServiceCaptureMethod.FieldKey_RequestHost],
                (table, columns) => $"WITH \"Latest\" AS (\r\n    SELECT {columns[1]}, MAX({columns[0]}) AS \"LatestId\"\r\n    FROM {table}\r\n    GROUP BY {columns[1]}\r\n)\r\nSELECT \"requests\".*\r\nFROM {table} AS \"requests\"\r\nJOIN \"Latest\" ON \"requests\".{columns[0]} = \"Latest\".\"LatestId\"\r\nORDER BY \"requests\".{columns[0]} DESC;");
            return examples.AsReadOnly();

            void Add(string title, string name, string[] required, Func<string, string[], string> create)
            {
                var table = schema.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (table == null)
                    return;
                var columns = required.Select(column => table.Columns.FirstOrDefault(item =>
                    item.Name.Equals(column, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (columns.Any(column => column == null))
                    return;
                examples.Add(new(Localization.GetLocalizedString(title),
                    create(SqlQueryText.QuoteIdentifier(table.Name),
                        columns.Select(column => SqlQueryText.QuoteIdentifier(column.Name)).ToArray())));
            }
        }

        internal static string Select(QueryTable table, bool preview)
        {
            var columns = table.Columns.Where(column => !column.Hidden)
                .Select(column => SqlQueryText.QuoteIdentifier(column.Name)).ToArray();
            return $"SELECT {(columns.Length == 0 ? "*" : string.Join(", ", columns))}\r\nFROM {SqlQueryText.QuoteIdentifier(table.Name)}" +
                (preview ? "\r\nLIMIT 1000;" : ";");
        }
    }
}
