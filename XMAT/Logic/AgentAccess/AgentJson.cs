// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace XMAT.AgentAccess
{
    internal static class AgentJson
    {
        internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        internal static JsonElement Encode<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    }
}
