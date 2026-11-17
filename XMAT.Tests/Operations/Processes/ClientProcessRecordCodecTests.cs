// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;

namespace XMAT.Tests.Operations.Processes
{
    public sealed class ClientProcessRecordCodecTests
    {
        [Theory]
        [InlineData(ClientProcessStatus.NotRecorded, "")]
        [InlineData(ClientProcessStatus.Local, "local")]
        [InlineData(ClientProcessStatus.Imported, "imported")]
        [InlineData(ClientProcessStatus.Remote, "remote")]
        [InlineData(ClientProcessStatus.Unresolved, "unresolved")]
        public void StatusToken_UsesTheFrozenTokens(ClientProcessStatus status, string expected)
        {
            Assert.Equal(expected, ClientProcessRecordCodec.StatusToken(status));
        }

        [Fact]
        public void FromValues_KeepsOnlyTheBasenameAndRoundTripsIdentity()
        {
            ClientProcessInfo info = ClientProcessRecordCodec.FromValues(
                @"C:\Games\Example\game.exe",
                4242,
                "2026-11-03T20:01:02.1234567+00:00",
                "local");

            Assert.Equal("game.exe", info.Name);
            Assert.Equal(4242, info.ProcessId);
            Assert.Equal(
                DateTimeOffset.Parse(
                    "2026-11-03T20:01:02.1234567+00:00",
                    System.Globalization.CultureInfo.InvariantCulture),
                info.StartTimeUtc);
            Assert.Equal(ClientProcessStatus.Local, info.Status);
        }

        [Theory]
        [InlineData("game.exe", -1, "", "local")]
        [InlineData("game.exe", 2147483648L, "", "local")]
        [InlineData("game.exe", 4, "not-a-time", "local")]
        [InlineData("game.exe", 4, "", "pending")]
        [InlineData("game\n.exe", 4, "", "local")]
        public void FromValues_InvalidMetadataDegradesConservatively(
            string name,
            long processId,
            string startTime,
            string status)
        {
            ClientProcessInfo info = ClientProcessRecordCodec.FromValues(name, processId, startTime, status);

            Assert.Equal(ClientProcessInfo.NotRecorded, info);
        }

        [Fact]
        public void Read_UsesOnlyTypedOptionalFields()
        {
            var record = new OptionalRecord(
                new Dictionary<string, object>
                {
                    [WebProxyCaptureSchema.ClientProcessName] =
                        "game.exe",
                    [WebProxyCaptureSchema.ClientProcessId] =
                        55L,
                    [WebProxyCaptureSchema.ClientProcessStartTime] =
                        "2026-11-03T20:01:02.0000000+00:00",
                    [WebProxyCaptureSchema.ClientProcessStatus] =
                        "imported",
                });

            ClientProcessInfo info =
                ClientProcessRecordCodec.Read(record);

            Assert.Equal("game.exe", info.Name);
            Assert.Equal(55, info.ProcessId);
            Assert.Equal(
                ClientProcessStatus.Imported,
                info.Status);
        }

        [Fact]
        public void Read_UntypedLegacyRecordHasNoProcessInformation()
        {
            Assert.Equal(ClientProcessInfo.NotRecorded, ClientProcessRecordCodec.Read(new LegacyRecord()));
        }

        [Fact]
        public void ToFields_NeverWritesNullOrPending()
        {
            IReadOnlyList<IFieldValue> fields = ClientProcessRecordCodec.ToFields(
                new ClientProcessInfo("game.exe", 77, null, ClientProcessStatus.Unresolved));

            Assert.Equal(4, fields.Count);
            Assert.All(fields, field => Assert.NotNull(field.Value));
            Assert.Equal(
                new[]
                {
                    WebProxyCaptureSchema.ClientProcessName,
                    WebProxyCaptureSchema.ClientProcessId,
                    WebProxyCaptureSchema.ClientProcessStartTime,
                    WebProxyCaptureSchema.ClientProcessStatus,
                },
                fields.Select(field => field.Name));
            Assert.DoesNotContain(
                fields,
                field => string.Equals(field.Value?.ToString(), "pending", StringComparison.Ordinal));
        }

        private sealed class OptionalRecord(IReadOnlyDictionary<string, object> values) : IDataRecord,
            IOptionalTypedDataRecord
        {
            public int RowId => 1;

            public long Int(string fieldName) => values.TryGetValue(fieldName, out object value) &&
                value is long number ? number : 0;

            public string Str(string fieldName) => values.TryGetValue(fieldName, out object value)
                ? value as string
                : null;

            public bool TryReadString(string name, out string value)
            {
                value = Str(name);
                return value != null;
            }

            public bool TryReadInt64(string name, out long value)
            {
                if (values.TryGetValue(name, out object stored) && stored is long number)
                {
                    value = number;
                    return true;
                }
                value = default;
                return false;
            }
        }

        private sealed class LegacyRecord : IDataRecord
        {
            public int RowId => 1;
            public long Int(string fieldName) => 0;
            public string Str(string fieldName) => null;
        }
    }
}
