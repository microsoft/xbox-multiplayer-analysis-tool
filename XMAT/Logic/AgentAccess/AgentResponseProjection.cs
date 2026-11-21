// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using XMAT.WebServiceCapture;

namespace XMAT.AgentAccess
{
    internal sealed class AgentResponseProjection(AgentDataPolicy policy, CancellationToken cancellationToken)
    {
        private static readonly HashSet<string> ControlFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "id", "sessionId", "captureId", "scriptId", "sourceId", "snapshotId", "resultId", "artifactId",
            "operationId", "targetId", "inputId", "folderId", "reportId", "requestHash", "sourceHash", "inputHash",
            "revision", "expectedRevision", "captureEpoch", "epoch", "cursor", "nextCursor", "sha256", "fingerprint",
            "argumentsHash", "confirmationToken", "encoding", "kind", "type", "state"
        };
        private long _redacted;
        private long _omitted;

        internal JsonElement Project(JsonElement content, string toolName)
        {
            var node = JsonNode.Parse(content.GetRawText());
            if (policy.Redacts && !IsProjectedTransfer(toolName))
                node = Visit(node, null, null, null, 0);
            else if (policy.Redacts && toolName == "xmat_read_script" && node is JsonObject scriptResult &&
                scriptResult["script"] is { } descriptor)
                scriptResult["script"] = Visit(descriptor, "script", null, null, 0);
            if (node is JsonObject root)
                root["dataSharing"] = new JsonObject
                {
                    ["view"] = policy.Redacts ? "redacted" : "complete",
                    ["bestEffort"] = policy.Redacts,
                    ["redactedValues"] = _redacted,
                    ["omittedValues"] = _omitted
                };
            return JsonSerializer.SerializeToElement(node, AgentJson.Options);
        }

        private JsonNode Visit(JsonNode node, string field, string column, string[] columns, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 64)
            {
                _omitted++;
                return JsonValue.Create("[OMITTED]");
            }
            if (node is JsonObject obj)
            {
                var localColumns = obj["columns"] is JsonArray schema
                    ? schema.Select(item => item is JsonObject descriptor ? descriptor["name"]?.GetValue<string>() : null).ToArray()
                    : columns;
                var blob = obj["kind"] is JsonValue kind && kind.TryGetValue<string>(out var kindName) && kindName == "Blob";
                var output = new JsonObject();
                foreach (var item in obj)
                {
                    if (blob && item.Key == "value")
                    {
                        output[item.Key] = string.Empty;
                        _omitted++;
                    }
                    else if (blob && item.Key == "byteLength")
                        output[item.Key] = 0;
                    else
                        output[item.Key] = Visit(item.Value, item.Key == "value" && column != null ? column : item.Key,
                            column, localColumns, depth + 1);
                }
                return output;
            }
            if (node is JsonArray array)
            {
                var output = new JsonArray();
                for (var index = 0; index < array.Count; index++)
                    output.Add(Visit(array[index], field, field is "cells" or "values" && columns != null && index < columns.Length
                        ? columns[index] : column, columns, depth + 1));
                return output;
            }
            if (node is not JsonValue value || field != null && ControlFields.Contains(field))
                return node?.DeepClone();
            if (value.TryGetValue<string>(out var text))
            {
                string filtered;
                if (field?.Equals("deviceName", StringComparison.OrdinalIgnoreCase) == true ||
                    field?.Equals("captureName", StringComparison.OrdinalIgnoreCase) == true)
                    filtered = policy.Pseudonym("device", text);
                else if (field?.EndsWith("IP", StringComparison.OrdinalIgnoreCase) == true ||
                    field?.EndsWith("Address", StringComparison.OrdinalIgnoreCase) == true)
                    filtered = policy.Pseudonym("address", text);
                else if (field != null && (field.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
                    field.Contains("body", StringComparison.OrdinalIgnoreCase) || field.Equals("bytes", StringComparison.OrdinalIgnoreCase)))
                {
                    filtered = "[OMITTED]";
                    _omitted++;
                }
                else
                    filtered = field != null && SazSanitizer.IsSensitiveField(field)
                        ? SazSanitizer.Redacted : policy.Text(text, cancellationToken);
                if (filtered != text)
                    _redacted++;
                return JsonValue.Create(filtered);
            }
            if (field != null && SazSanitizer.IsSensitiveField(field) && value.GetValueKind() == JsonValueKind.Number)
            {
                _redacted++;
                return JsonValue.Create(0);
            }
            return node.DeepClone();
        }

        private static bool IsProjectedTransfer(string name) => name is
            "xmat_get_query_page" or "xmat_get_query_value" or "xmat_get_query_export_chunk" or "xmat_read_script";
    }
}
