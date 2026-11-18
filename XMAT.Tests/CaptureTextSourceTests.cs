// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class CaptureTextSourceTests
    {
        [Fact]
        public async Task ActiveAndUnknownSourcesHaveFixedClassification()
        {
            using var database = new SqLtDatabase();
            database.Initialize();

            Assert.Equal(
                CaptureTextSemantics.Literal,
                database.CaptureTextSource.Semantics);
            Assert.Null(database.CaptureTextSource.Identity);
            Assert.False(database.CaptureTextSource.IsBindable);
            Assert.Same(
                database.CaptureTextSource,
                await database.CaptureTextSource.BindAsync(
                    CancellationToken.None));
            IDataTable table =
                database.CreateTable(
                    "Source",
                    new Field<string>("Value"));
            Assert.Same(
                database.CaptureTextSource,
                Assert.IsAssignableFrom<ICaptureTextSourceProvider>(
                    table)
                    .CaptureTextSource);
            Assert.Same(
                database.CaptureTextSource,
                Assert.IsAssignableFrom<ICaptureTextSourceProvider>(
                    table.Dataset)
                    .CaptureTextSource);

            Assert.Equal(
                CaptureTextSemantics.AmbiguousUnmarked,
                CaptureTextSource.Unknown.Semantics);
            Assert.Null(CaptureTextSource.Unknown.Identity);
            Assert.False(CaptureTextSource.Unknown.IsBindable);
            CaptureTextSourceException exception =
                await Assert.ThrowsAsync<CaptureTextSourceException>(
                    async () =>
                    {
                        await CaptureTextSource.Unknown.BindAsync(
                            CancellationToken.None);
                    });
            Assert.Equal(
                CaptureTextSourceException.SourceUnavailableCode,
                exception.Code);
            Assert.Equal(
                CaptureTextSourceException.SourceUnavailableMessage,
                exception.Message);
        }

        [Fact]
        public async Task ReadOnlySourceFlowsToRowsAndModels_AndBindsTheHeldFile()
        {
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "marked.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1));
            CaptureTextSource source;
            using (var database =
                new SqLtReadonlyDatabase(path))
            {
                source = database.CaptureTextSource;
                Assert.Equal(
                    CaptureTextSemantics.Literal,
                    source.Semantics);
                Assert.Equal(
                    (SqLtFileIdentity?)database.Identity,
                    source.Identity);
                Assert.True(source.IsBindable);

                IReadonlyDataTable table =
                    database.TableByName(
                        "WebProxyConnections");
                Assert.Same(
                    source,
                    Assert.IsAssignableFrom<ICaptureTextSourceProvider>(
                        table)
                        .CaptureTextSource);
                IDataset dataset =
                    table.Dataset;
                Assert.Same(
                    source,
                    Assert.IsAssignableFrom<ICaptureTextSourceProvider>(
                        dataset)
                        .CaptureTextSource);
                IEnumerable<IDataRecord> records =
                    dataset.DataRecords;
                Assert.Same(
                    source,
                    Assert.IsAssignableFrom<ICaptureTextSourceProvider>(
                        records)
                        .CaptureTextSource);
                IDataRecord record =
                    Assert.Single(records);
                Assert.Same(
                    source,
                    Assert.IsAssignableFrom<ICaptureTextSourceProvider>(
                        record)
                        .CaptureTextSource);

                var model =
                    new ProxyConnectionModel(record);
                Assert.Same(
                    source,
                    model.CaptureTextSource);
                Assert.Same(
                    source,
                    new ProxyConnectionModel(model)
                        .CaptureTextSource);

                CaptureTextSource bound =
                    await source.BindAsync(
                        CancellationToken.None);
                Assert.Same(source, bound);
                Assert.Equal(
                    Convert.ToHexString(
                            SHA256.HashData(
                                File.ReadAllBytes(path)))
                        .ToLowerInvariant(),
                    source.Sha256);
            }

            Assert.False(source.IsBindable);
            CaptureTextSourceException exception =
                await Assert.ThrowsAsync<CaptureTextSourceException>(
                    async () =>
                    {
                        await source.BindAsync(
                            CancellationToken.None);
                    });
            Assert.Equal(
                CaptureTextSourceException.SourceUnavailableCode,
                exception.Code);
        }

        [Fact]
        public void UnmarkedFileAndUnclassifiedRecordAreAmbiguous()
        {
            const string Headers =
                "GET / HTTP/1.1\r\n" +
                "If-None-Match: \"v1\"\r\n" +
                "X-Adjacent: \"\"\r\n";
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "unmarked.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            CaptureTextFormat.AmbiguousUserVersion));
            using (var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Pooling = false,
                    }.ToString()))
            {
                connection.Open();
                using var update =
                    connection.CreateCommand();
                update.CommandText =
                    "UPDATE WebProxyConnections " +
                    "SET RequestLineAndHeaders=$headers";
                update.Parameters.AddWithValue(
                    "$headers",
                    Headers);
                update.ExecuteNonQuery();
            }
            using var database =
                new SqLtReadonlyDatabase(path);
            Assert.Equal(
                CaptureTextSemantics.AmbiguousUnmarked,
                database.CaptureTextSource.Semantics);
            IDataRecord stored =
                Assert.Single(
                    database.TableByName(
                            "WebProxyConnections")
                        .Dataset.DataRecords);
            Assert.Equal(
                Headers,
                stored.Str(
                    "RequestLineAndHeaders"));
            Assert.Same(
                database.CaptureTextSource,
                new ProxyConnectionModel(stored)
                    .CaptureTextSource);

            var model =
                new ProxyConnectionModel(
                    new UnclassifiedRecord());
            Assert.Same(
                CaptureTextSource.Unknown,
                model.CaptureTextSource);
            var table = new CountingTable();

            Assert.Throws<InvalidOperationException>(
                () => model.AddToDataTable(
                    "device",
                    table));
            Assert.Equal(0, table.AddCount);
        }

        [Fact]
        public async Task BindingRunsOnAWorkerCachesTheHashAndMatchesIdentity()
        {
            var started =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var release =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var identity =
                new SqLtFileIdentity(
                    7,
                    11);
            CaptureTextSource source =
                CaptureTextSource.CreateFile(
                    CaptureTextSemantics.AmbiguousUnmarked,
                    identity,
                    _ =>
                    {
                        Interlocked.Increment(
                            ref calls);
                        started.TrySetResult();
                        release.Task.GetAwaiter().GetResult();
                        return new byte[32];
                    },
                    () => true);

            ValueTask<CaptureTextSource> binding =
                source.BindAsync(
                    CancellationToken.None);
            await started.Task;
            Assert.False(binding.IsCompleted);
            release.TrySetResult();
            Assert.Same(source, await binding);
            Assert.Equal(1, calls);
            Assert.Same(
                source,
                await source.BindAsync(
                    CancellationToken.None));
            Assert.Equal(1, calls);

            CaptureTextSource same =
                CaptureTextSource.CreateFile(
                    CaptureTextSemantics.AmbiguousUnmarked,
                    identity,
                    _ => new byte[32],
                    () => true);
            CaptureTextSource anotherFile =
                CaptureTextSource.CreateFile(
                    CaptureTextSemantics.AmbiguousUnmarked,
                    new SqLtFileIdentity(
                        7,
                        12),
                    _ => new byte[32],
                    () => true);
            CaptureTextSource anotherHash =
                CaptureTextSource.CreateFile(
                    CaptureTextSemantics.AmbiguousUnmarked,
                    identity,
                    _ =>
                    {
                        var hash = new byte[32];
                        hash[0] = 1;
                        return hash;
                    },
                    () => true);
            CaptureTextSource unbound =
                CaptureTextSource.CreateFile(
                    CaptureTextSemantics.AmbiguousUnmarked,
                    identity,
                    _ => new byte[32],
                    () => true);
            CaptureTextSource literal =
                CaptureTextSource.CreateFile(
                    CaptureTextSemantics.Literal,
                    identity,
                    _ => new byte[32],
                    () => true);
            await same.BindAsync(
                CancellationToken.None);
            await anotherFile.BindAsync(
                CancellationToken.None);
            await anotherHash.BindAsync(
                CancellationToken.None);
            await literal.BindAsync(
                CancellationToken.None);

            Assert.True(source.Matches(same));
            Assert.True(same.Matches(source));
            Assert.False(source.Matches(anotherFile));
            Assert.False(source.Matches(anotherHash));
            Assert.False(source.Matches(unbound));
            Assert.False(unbound.Matches(source));
            Assert.False(unbound.Matches(unbound));
            Assert.False(source.Matches(literal));
            Assert.False(source.Matches(CaptureTextSource.Unknown));
            Assert.False(
                CaptureTextSource.Unknown.Matches(
                    CaptureTextSource.Unknown));
        }

        [Fact]
        public async Task BindingCancellationLeavesTheSourceUnboundAndRetryable()
        {
            var started =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            CaptureTextSource source =
                CaptureTextSource.CreateFile(
                    CaptureTextSemantics.AmbiguousUnmarked,
                    new SqLtFileIdentity(
                        3,
                        5),
                    token =>
                    {
                        if (token.CanBeCanceled)
                        {
                            started.TrySetResult();
                            token.WaitHandle.WaitOne();
                            token.ThrowIfCancellationRequested();
                        }
                        return new byte[32];
                    },
                    () => true);
            using var cancellation =
                new CancellationTokenSource();

            ValueTask<CaptureTextSource> binding =
                source.BindAsync(
                    cancellation.Token);
            await started.Task;
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () =>
                {
                    await binding;
                });
            Assert.Null(source.Sha256);

            Assert.Same(
                source,
                await source.BindAsync(
                    CancellationToken.None));
            Assert.NotNull(source.Sha256);
        }

        [Fact]
        public void DirectAndCopiedModelsAreLiteral()
        {
            var direct =
                new ProxyConnectionModel(
                    1,
                    "200",
                    DateTime.UnixEpoch,
                    TimeSpan.Zero,
                    "https",
                    "example.test",
                    "GET",
                    "/",
                    "GET / HTTP/1.1\r\nHost: example.test\r\n",
                    Array.Empty<byte>(),
                    "HTTP/1.1 200 OK\r\n",
                    Array.Empty<byte>(),
                    "10.0.0.1");
            Assert.Same(
                CaptureTextSource.Literal,
                direct.CaptureTextSource);
            Assert.Same(
                CaptureTextSource.Literal,
                new ProxyConnectionModel(direct)
                    .CaptureTextSource);

            var table = new CountingTable();
            direct.AddToDataTable(
                "device",
                table);
            Assert.Equal(1, table.AddCount);
        }

        private sealed class UnclassifiedRecord : IDataRecord
        {
            public int RowId => 1;

            public long Int(string fieldName) =>
                fieldName == "RequestNumber"
                    ? 1
                    : 0;

            public string Str(string fieldName) =>
                fieldName switch
                {
                    "RequestTimestamp" =>
                        DateTime.UnixEpoch.ToString("o"),
                    "ResponseTimestamp" =>
                        DateTime.UnixEpoch.ToString("o"),
                    _ => string.Empty,
                };
        }

        private sealed class CountingTable : IDataTable
        {
            internal int AddCount { get; private set; }

            public event Action<IDataTable, long> RowAdded
            {
                add { }
                remove { }
            }

            public event Action<IDataTable, long> RowUpdated
            {
                add { }
                remove { }
            }

            public event Action<IDataTable, long> RowsRemoved
            {
                add { }
                remove { }
            }

            public string Name => "Counting";

            public IDataset Dataset { get; } = new EmptyDataset();

            public IDataset Subset(params string[] fieldNames) =>
                throw new NotSupportedException();

            public long AddRow(params object[] fieldValueArgs)
            {
                AddCount++;
                return AddCount;
            }

            public void UpdateRow(
                long rowId,
                params IFieldValue[] fieldNamesAndValues) =>
                throw new NotSupportedException();

            public void RemoveRowsWhere(
                params IFieldValue[] fieldNamesAndValues) =>
                throw new NotSupportedException();
        }

        private sealed class EmptyDataset : IDataset
        {
            public IEnumerable<IFieldDefinition> FieldDefs => Array.Empty<IFieldDefinition>();

            public IEnumerable<IDataRecord> DataRecords => Array.Empty<IDataRecord>();

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId) => DataRecords;

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId, int limit) => DataRecords;

            public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] filters) => DataRecords;
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory(
                bool shortPath = false)
            {
                Path = shortPath
                    ? System.IO.Path.Join(
                        System.IO.Path.GetTempPath(),
                        "xmat-path-" +
                        Guid.NewGuid().ToString("N"))
                    : System.IO.Path.Join(
                        AppContext.BaseDirectory,
                        "DatabaseSaveFixtures",
                        Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
        }
    }
}
