// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class AgentDataProjectionTests
    {
        private static readonly QueryExecutionOptions Options = new(TimeSpan.FromSeconds(10), 2 * 1024 * 1024);

        [Fact]
        public async Task PresentationAliasesResolveBeforeTypedFilterValidationAndExpireWithTheDataView()
        {
            using var directory = new TestDirectory();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await using var runtime = new AgentRuntime(policy);
            var filter = new AgentCaptureDataFilter
            {
                DeviceName = runtime.DataPolicy.Pseudonym("device", "private-console"),
                SourceAddress = runtime.DataPolicy.Pseudonym("address", "192.0.2.45"),
                Method = "POST"
            };
            var resolved = filter.Resolve(runtime.DataPolicy);
            Assert.Equal("private-console", resolved.DeviceName);
            Assert.Equal("192.0.2.45", resolved.SourceAddress);
            Assert.Equal("POST", resolved.Method);
            await policy.SaveAsync(policy.Current.Settings with { Enabled = true }, 0);
            await runtime.ApplyPolicyAsync();
            await policy.SaveAsync(policy.Current.Settings with { DataSharing = AgentDataSharing.Complete }, policy.Current.Settings.Revision);
            await runtime.ApplyPolicyAsync();
            await policy.SaveAsync(policy.Current.Settings with { DataSharing = AgentDataSharing.Redacted }, policy.Current.Settings.Revision);
            await runtime.ApplyPolicyAsync();
            Assert.Equal(AgentAccessError.SourceChanged,
                Assert.Throws<AgentAccessException>(() => filter.Resolve(runtime.DataPolicy)).Error);
        }

        [Fact]
        public async Task SourceDefaultsAreNotCopiedIntoTheFilteredSchemaOrExecuted()
        {
            using var directory = new TestDirectory();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await using var runtime = new AgentRuntime(policy);
            using var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable("Records", new Field<long>("Value")).AddRow(3L);
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(SqLtDatabase)
                .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(database);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ALTER TABLE Records ADD COLUMN AuthToken TEXT DEFAULT 'schema-private-829175'";
                command.ExecuteNonQuery();
            }
            await using var snapshot = await database.CreateQuerySessionAsync(
                runtime.DataPolicy.CreateSnapshotProjection(2 * 1024 * 1024));
            await using var result = await snapshot.ExecuteAsync(
                "SELECT sql,(SELECT AuthToken FROM Records) FROM sqlite_schema WHERE name='Records'", Options);
            var cells = (await result.ReadPageAsync(0, 1, 64 * 1024)).Rows[0].Cells;
            Assert.DoesNotContain("schema-private-829175", (string)cells[0].Value);
            Assert.Equal(SazSanitizer.Redacted, cells[1].Value);
        }

        [Fact]
        public async Task OutboundProjectionMasksDetailsAndOpaqueCellsButPreservesControlIdentitiesAndProjectedBytes()
        {
            using var directory = new TestDirectory();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await using var runtime = new AgentRuntime(policy);
            var id = Guid.NewGuid();
            const string cursor = "opaque//transport+cursor==";
            var original = AgentJson.Encode(new
            {
                id,
                cursor,
                deviceName = "private-console",
                apiKey = "private-key-983152",
                columns = new[] { new { name = "ResponseBody" } },
                rows = new[] { new { cells = new[] { new { kind = "Text", value = Convert.ToBase64String(Encoding.UTF8.GetBytes("private-body")) } } } },
                diagnostic = "Authorization: Bearer private-diagnostic-192837",
                blob = new { kind = "Blob", value = Encoding.UTF8.GetBytes("private-blob"), byteLength = 12 }
            });
            var projected = new AgentResponseProjection(runtime.DataPolicy, default).Project(original, "xmat_get_data_details");
            Assert.Equal(id, projected.GetProperty("id").GetGuid());
            Assert.Equal(cursor, projected.GetProperty("cursor").GetString());
            Assert.StartsWith("device-", projected.GetProperty("deviceName").GetString());
            Assert.DoesNotContain("private-", projected.GetRawText());
            Assert.Equal("[OMITTED]", projected.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("value").GetString());
            Assert.Equal(string.Empty, projected.GetProperty("blob").GetProperty("value").GetString());
            var bytes = AgentJson.Encode(new { bytes = Encoding.UTF8.GetBytes("already filtered bytes"), cursor });
            var transfer = new AgentResponseProjection(runtime.DataPolicy, default).Project(bytes, "xmat_get_query_value");
            Assert.Equal(bytes.GetProperty("bytes").GetString(), transfer.GetProperty("bytes").GetString());
            Assert.Equal(cursor, transfer.GetProperty("cursor").GetString());
            Assert.Equal("17", runtime.DataPolicy.Text("17"));
        }

        [Fact]
        public async Task FilteringPrecedesSqlTransformsChunksAndExportsWithoutMutatingTheSource()
        {
            using var directory = new TestDirectory();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await using var runtime = new AgentRuntime(policy);
            using var database = new SqLtDatabase();
            database.Initialize();
            const string secret = "original-private-key-184236";
            var detail = new string('x', 20000) + "\nAuthorization: Bearer detail-private-571923\n";
            var table = database.CreateTable("Records", new Field<string>("ApiKey"),
                new Field<string>("Detail"), new Field<long>("Count"));
            table.AddRow(secret, detail, 7L);
            var projection = runtime.DataPolicy.CreateSnapshotProjection(2 * 1024 * 1024);
            await using var snapshot = await database.CreateQuerySessionAsync(projection);
            await using var result = await snapshot.ExecuteAsync(
                "SELECT id,hex(ApiKey),ApiKey || ':' || Count AS arbitrary,Detail,typeof(Count),Count FROM Records", Options);
            var page = await result.ReadPageAsync(0, 1, 64 * 1024);
            Assert.Equal(1L, page.Rows[0].Cells[0].Value);
            Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(SazSanitizer.Redacted)), page.Rows[0].Cells[1].Value);
            Assert.Equal(SazSanitizer.Redacted + ":7", page.Rows[0].Cells[2].Value);
            Assert.Equal("integer", page.Rows[0].Cells[4].Value);
            Assert.Equal(7L, page.Rows[0].Cells[5].Value);
            var chunk = await result.ReadValueAsync(0, 3, 0, 32768);
            Assert.DoesNotContain("detail-private-571923", Encoding.UTF8.GetString(chunk.Bytes));
            Assert.Contains(SazSanitizer.Redacted, Encoding.UTF8.GetString(chunk.Bytes));
            using var export = new MemoryStream();
            await QueryResultExporter.WriteAsync(result, export, QueryExportFormat.Json, 2 * 1024 * 1024);
            var exported = Encoding.UTF8.GetString(export.ToArray());
            Assert.DoesNotContain(secret, exported);
            Assert.DoesNotContain("detail-private-571923", exported);
            var workspace = (string)snapshot.GetType().GetField("_workspace",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(snapshot);
            foreach (var file in Directory.EnumerateFiles(workspace, "*.sqlite"))
            {
                var bytes = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                Assert.DoesNotContain(secret, bytes);
                Assert.DoesNotContain("detail-private-571923", bytes);
            }
            Assert.Equal(secret, table.Dataset.DataRecords.Single().Str("ApiKey"));
            await using var original = await database.CreateQuerySessionAsync();
            await using var originalResult = await original.ExecuteAsync("SELECT ApiKey FROM Records", Options);
            Assert.Equal(secret, (await originalResult.ReadPageAsync(0, 1, 64 * 1024)).Rows[0].Cells[0].Value);
            Assert.True(projection.RedactedValues > 0);
            await Assert.ThrowsAsync<QueryException>(() => snapshot.ExecuteAsync("DELETE FROM Records", Options));
        }

        [Fact]
        public async Task Base64HttpBodiesAreDecodedAndFilteredAndOpaqueResponsesAreOmittedBeforeSql()
        {
            using var directory = new TestDirectory();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await using var runtime = new AgentRuntime(policy);
            using var database = new SqLtDatabase();
            database.Initialize();
            var request = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"password\":\"body-private-983251\",\"count\":17}"));
            var response = Convert.ToBase64String(Encoding.UTF8.GetBytes("opaque-private-572491"));
            database.CreateTable(WebServiceCaptureMethod.TableName,
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod)).AddRow(
                    "POST / HTTP/1.1\r\nAuthorization: Bearer header-private-872341\r\nContent-Type: application/json\r\n\r\n",
                    request, "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n\r\n", response, "POST");
            var projection = runtime.DataPolicy.CreateSnapshotProjection(2 * 1024 * 1024);
            await using var snapshot = await database.CreateQuerySessionAsync(projection);
            await using var result = await snapshot.ExecuteAsync(
                $"SELECT \"{WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders}\"," +
                $"\"{WebServiceCaptureMethod.FieldKey_RequestBody}\",\"{WebServiceCaptureMethod.FieldKey_ResponseBody}\" " +
                $"FROM \"{WebServiceCaptureMethod.TableName}\"", Options);
            var cells = (await result.ReadPageAsync(0, 1, 64 * 1024)).Rows[0].Cells;
            Assert.DoesNotContain("header-private-872341", (string)cells[0].Value);
            var filtered = Encoding.UTF8.GetString(Convert.FromBase64String((string)cells[1].Value));
            Assert.DoesNotContain("body-private-983251", filtered);
            Assert.Contains("17", filtered);
            Assert.Equal(string.Empty, cells[2].Value);
            Assert.True(projection.OmittedValues > 0);
            Assert.Equal(request, database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords.Single()
                .Str(WebServiceCaptureMethod.FieldKey_RequestBody));
        }

        [Fact]
        public async Task DeviceAliasesJoinConsistentlyAndPacketPayloadsAreNotQueryableAsEncodedOriginals()
        {
            using var directory = new TestDirectory();
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await using var runtime = new AgentRuntime(policy);
            using var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable("Devices", new Field<string>("DeviceName")).AddRow("private-console");
            database.CreateTable("Packets", new Field<string>("DeviceName"),
                new Field<string>("Payload"), new Field<string>("ClientIP")).AddRow(
                    "private-console", Convert.ToBase64String(Encoding.UTF8.GetBytes("packet-private-891237")), "192.0.2.45");
            await using var snapshot = await database.CreateQuerySessionAsync(
                runtime.DataPolicy.CreateSnapshotProjection(2 * 1024 * 1024));
            await using var result = await snapshot.ExecuteAsync(
                "SELECT d.DeviceName,p.ClientIP,hex(p.Payload),count(*) " +
                "FROM Devices d JOIN Packets p ON d.DeviceName=p.DeviceName", Options);
            var cells = (await result.ReadPageAsync(0, 1, 64 * 1024)).Rows[0].Cells;
            Assert.StartsWith("device-", (string)cells[0].Value);
            Assert.Equal(runtime.DataPolicy.Pseudonym("device", "private-console"), cells[0].Value);
            Assert.StartsWith("address-", (string)cells[1].Value);
            Assert.Equal(string.Empty, cells[2].Value);
            Assert.Equal(1L, cells[3].Value);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task BrokenProjectionOrQuotaFailsWithoutReturningARawSession(bool identity)
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            var table = database.CreateTable("Data", new Field<string>("Name"));
            table.AddRow("original");
            var projection = new TestProjection(identity ? 1024 * 1024 : 1, (_, row, _) =>
            {
                if (identity)
                    row[0] = 999L;
                return row;
            });
            var failure = await Assert.ThrowsAsync<QueryException>(() => database.CreateQuerySessionAsync(projection));
            Assert.Equal(identity ? QueryError.ProjectionUnavailable : QueryError.ResultLimit, failure.Error);
            Assert.Equal("original", table.Dataset.DataRecords.Single().Str("Name"));
        }

        [Fact]
        public async Task UnsupportedProvidersFailClosedInsteadOfUsingTheirUnfilteredSnapshot()
        {
            IQueryDatabase unsupported = new UnsupportedDatabase();
            var failure = await Assert.ThrowsAsync<QueryException>(() =>
                unsupported.CreateQuerySessionAsync(new TestProjection(1024, (_, row, _) => row)));
            Assert.Equal(QueryError.ProjectionUnavailable, failure.Error);
        }

        private sealed record TestProjection(long MaximumBytes,
            Func<QueryTable, object[], CancellationToken, object[]> Apply) : IQuerySnapshotProjection
        {
            public object[] Project(QueryTable table, object[] values, CancellationToken cancellationToken) =>
                Apply(table, values, cancellationToken);
        }

        private sealed class UnsupportedDatabase : IQueryDatabase
        {
            public Task<IReadOnlyList<QueryTable>> GetQuerySchemaAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<QueryTable>>([]);
            public Task<IQuerySession> CreateQuerySessionAsync(CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("The unfiltered path must never be called.");
        }

        private sealed class TestDirectory : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XMAT.Projection." + Guid.NewGuid().ToString("N"));
            internal TestDirectory() => Directory.CreateDirectory(Path);
            public void Dispose() => Directory.Delete(Path, true);
        }
    }
}
