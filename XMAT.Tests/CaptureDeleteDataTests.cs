// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.Tests.Operations.Export;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public sealed class CaptureDeleteDataTests
    {
        private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

        [Fact]
        public void DeleteRows_DeletesOnlyTheExactDeviceAndReturnsCommittedIds()
        {
            using SqLtDatabase database = Database(out IDataTable captures);
            long first = captures.AddRow("console'a", "first");
            long retained = captures.AddRow("console'a", "retained");
            long other = captures.AddRow("console-b", "other");

            IReadOnlyList<long> deleted = Delete(captures, "console'a", first, other, 999_999, first);

            Assert.Equal(new[] { first }, deleted.OrderBy(id => id));
            Assert.Equal(new[] { retained, other }, Ids(captures));
        }

        [Fact]
        public void DeleteRows_StatementFailureLeavesEveryRequestedRow()
        {
            using SqLtDatabase database = Database(out IDataTable captures);
            long first = captures.AddRow("console-a", "first");
            long second = captures.AddRow("console-a", "second");
            Execute(Connection(database),
                "CREATE TEMP TRIGGER FailExactDelete BEFORE DELETE ON WebProxyConnections " +
                "WHEN old.Payload = 'second' BEGIN SELECT RAISE(ABORT, 'injected delete failure'); END");

            Assert.Throws<SqliteException>(() => Delete(captures, "console-a", first, second));

            Assert.Equal(new[] { first, second }, Ids(captures));
        }

        [Fact]
        public async Task DeleteRows_ConcurrentOtherTableWriteSucceeds()
        {
            using SqLtDatabase database = Database(out IDataTable captures);
            IDataTable trace = database.CreateTable("NetworkTracePackets", new Field<string>("Payload"));
            long rowId = captures.AddRow("console-a", "delete");
            SqliteConnection connection = Connection(database);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            connection.CreateFunction<long, long>("hold_exact_delete", value =>
            {
                entered.TrySetResult();
                if (!release.Wait(CompletionTimeout))
                {
                    throw new TimeoutException("The exact-delete test callback was not released.");
                }
                return value;
            });
            Execute(connection,
                "CREATE TEMP TRIGGER HoldExactDelete BEFORE DELETE ON WebProxyConnections " +
                "BEGIN SELECT hold_exact_delete(old.id); END");

            Task<IReadOnlyList<long>> deletion = Task.Run(() => Delete(captures, "console-a", rowId));
            Task<long> writer = null;
            try
            {
                Assert.Same(
                    entered.Task,
                    await Task.WhenAny(deletion, entered.Task).WaitAsync(CompletionTimeout));
                var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                writer = Task.Run(() =>
                {
                    writerStarted.SetResult();
                    return trace.AddRow("packet");
                });
                await writerStarted.Task.WaitAsync(CompletionTimeout);
                release.Set();
                await Task.WhenAll(deletion, writer).WaitAsync(CompletionTimeout);

                Assert.Equal(new[] { rowId }, await deletion);
                Assert.True(await writer > 0);
                Assert.Single(trace.Dataset.DataRecords);
            }
            finally
            {
                release.Set();
                await ObserveStartedWorkAsync(
                    writer == null ? deletion : Task.WhenAll(deletion, writer));
            }
        }

        [Fact]
        public void WritableDatabase_EnablesAndVerifiesSecureDeleteBeforeWrites()
        {
            using var database = UnconfiguredDatabase();
            database.Initialize();

            Assert.Equal(1L, Scalar(Connection(database), "PRAGMA secure_delete"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DeleteOrClearThenSave_DoesNotRetainAnOverflowCanary(bool exactDelete)
        {
            using var directory = new ExportTestDirectory();
            using SqLtDatabase database = Database(out IDataTable captures);
            string marker = "deleted-" + Guid.NewGuid().ToString("N");
            long rowId = captures.AddRow("console-a", marker + new string('x', 256 * 1024));

            if (exactDelete)
                _ = Delete(captures, "console-a", rowId);
            else
                captures.RemoveRowsWhere();

            string path = directory.PathOf("after-delete.xmatcap");
            database.SaveToFile(path);
            Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) < 0);
        }

        [Fact]
        public void DeleteRows_FreedOverflowPagesAreReused()
        {
            using SqLtDatabase database = Database(out IDataTable captures);
            SqliteConnection connection = Connection(database);
            string payload = new('x', 256 * 1024);
            long rowId = captures.AddRow("console-a", payload);
            long populated = Scalar(connection, "PRAGMA page_count");

            _ = Delete(captures, "console-a", rowId);
            long freed = Scalar(connection, "PRAGMA freelist_count");
            _ = captures.AddRow("console-b", new string('y', payload.Length));

            Assert.True(freed > 0);
            Assert.True(Scalar(connection, "PRAGMA page_count") <= populated + 1);
            Assert.True(Scalar(connection, "PRAGMA freelist_count") < freed);
        }

        private static SqLtDatabase Database(out IDataTable captures)
        {
            SqLtDatabase database = UnconfiguredDatabase();
            database.Initialize();
            captures = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                new Field<string>("Payload"));
            return database;
        }

        private static async Task ObserveStartedWorkAsync(Task task)
        {
            try
            {
                await task.WaitAsync(CompletionTimeout);
            }
            catch (Exception exception) when (
                exception is not TimeoutException &&
                !ExceptionUtilities.IsFatal(exception))
            {
                // Cleanup only observes a worker failure already owned by the primary test path.
            }
        }

        private static SqLtDatabase UnconfiguredDatabase() =>
            new()
            {
                WritableConnectionOpened = connection => Execute(connection, "PRAGMA secure_delete=OFF"),
            };

        private static IReadOnlyList<long> Delete(IDataTable table, string device, params long[] ids) =>
            ((IExactCaptureRowDelete)table).DeleteRows(device, ids);

        private static IEnumerable<long> Ids(IDataTable table) =>
            table.Dataset.DataRecords.Select(record => (long)record.RowId).OrderBy(id => id);

        private static SqliteConnection Connection(SqLtDatabase database) =>
            (SqliteConnection)typeof(SqLtDatabase)
                .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(database);

        private static void Execute(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static long Scalar(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }
}
