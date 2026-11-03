// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class SqLtCapturePublicationTests
    {
        [Fact]
        public async Task AtomicPublicationCoordinatesOtherTablesWithoutMaterializingTheirReaders()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            var captured = database.CreateTable("Captured", new Field<string>("Device"), new Field<string>("Value"));
            var other = database.CreateTable("Other", new Field<string>("Value"));
            captured.AddRow("one", "before");
            other.AddRow("old");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            IEnumerable<object[]> Rows()
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Publication was not released.");
                yield return ["one", "after"];
            }
            var publishing = Task.Run(() => ((IImportedCaptureTable)captured).ReplaceImportedRows(
                new FieldValue<string>("Device", "one"), Rows()));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var writing = Task.Run(() => other.AddRow("new"));
            var reading = Task.Run(() => other.Dataset.DataRecords.ToArray());
            try
            {
                var delay = Task.Delay(100);
                Assert.Same(delay, await Task.WhenAny(writing, delay));
                Assert.False(reading.IsCompleted);
            }
            finally { release.Set(); }
            await Task.WhenAll(publishing, writing, reading).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("after", captured.Dataset.DataRecords.Single().Str("Value"));
            Assert.Equal(new[] { "old", "new" }, other.Dataset.DataRecords.Select(record => record.Str("Value")));
        }
    }
}
