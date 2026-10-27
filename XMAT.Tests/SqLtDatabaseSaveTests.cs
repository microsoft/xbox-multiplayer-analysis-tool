// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class SqLtDatabaseSaveTests
    {
        [Fact]
        public void SaveToFile_ReleasesDestinationAndProducesReadableDatabase()
        {
            using var directory = new TestDirectory();
            var path = Path.Join(directory.Path, "saved.xmatcap");
            using (var database = new SqLtDatabase())
            {
                database.Initialize();
                var table = database.CreateTable(
                    "SavedData",
                    new Field<string>("Value"));
                table.AddRow("expected");

                database.SaveToFile(path);
                table.AddRow("updated");
                database.SaveToFile(path);
            }

            using (var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.True(exclusive.Length > 0);
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using (var saved = new SqliteConnection(connectionString))
            {
                saved.Open();
                using var command = saved.CreateCommand();
                command.CommandText = "SELECT Value FROM SavedData ORDER BY rowid";
                using var reader = command.ExecuteReader();
                var values = new List<string>();
                while (reader.Read())
                {
                    values.Add(reader.GetString(0));
                }
                Assert.Equal(new[] { "expected", "updated" }, values);
            }

            File.Delete(path);
            Assert.False(File.Exists(path));
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
            {
                Path = System.IO.Path.Join(
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
