using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task WithBuilderSchema(bool mysql, byte[][] columns, string?[][] keys,
            Action<OceanBaseDataAdapter, OceanBaseCommandBuilder> verify, bool distinct = false, bool transaction = false, int snapshots = 1)
        {
            var server = Server(async channel =>
            {
                if (transaction) await TransactionBegin(channel);
                await StatementCommand(channel, 22); await KeyPrepare(channel, 91, columns);
                for (var i = 0; i < snapshots; i++) if (!distinct) await KeyDirectory(channel, mysql, keys);
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
                if (transaction) { channel.BeginCommand(); await TransactionEnd(channel, false); }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var local = transaction ? connection.BeginTransaction(IsolationLevel.ReadCommitted) : null;
                using var select = Assert.IsType<OceanBaseCommand>(connection.CreateCommand());
                select.Transaction = local;
                select.CommandText = "select " + (distinct ? "distinct " : "") + "* from " + (mysql ? "`租户库`.ledger" : "\"租户库\".\"ledger\"");
                using var adapter = new OceanBaseDataAdapter(select);
                using var builder = new OceanBaseCommandBuilder(adapter);
                verify(adapter, builder);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        private static string?[] BuilderKey(string column, string ordinal = "1", string nullable = "NO", string kind = "P")
            => new string?[] { "租户库", "ledger", "K", kind, column, ordinal, nullable, "YES" };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderTemporalBindingsRetainFractionalValues(bool mysql)
        {
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id"), SchemaColumn("stamp", mysql ? (byte)7 : (byte)202, 29, originalName: "stamp") };
            await WithBuilderSchema(mysql, columns, new[] { BuilderKey("id") }, (_, builder) =>
            {
                var insert = builder.GetInsertCommand();
                var timestamp = new DateTime(2026, 10, 25, 1, 2, 3).AddTicks(1234560);
                insert.Parameters[0].Value = 1L; insert.Parameters[1].Value = timestamp;
                var actual = new[] { (OceanBaseParameter)insert.Parameters[0], (OceanBaseParameter)insert.Parameters[1] };
                var expected = new[] { new OceanBaseParameter { Value = 1L }, new OceanBaseParameter { Value = timestamp, OceanBaseDbType = OceanBaseDbType.Timestamp } };
                var mode = mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
                Assert.Equal(PreparedParameters.Encode(expected, mode), PreparedParameters.Encode(actual, mode));
                actual[1].Scale = 0;
                Assert.Same(insert, builder.GetInsertCommand());
                actual[0].Value = 1L; actual[1].Value = timestamp;
                Assert.Equal(PreparedParameters.Encode(expected, mode), PreparedParameters.Encode(actual, mode));
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderUnknownDecimalScaleDoesNotInventAnIntegerDeclaration(bool mysql)
        {
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id"), SchemaColumn("amount", 246, 0, 255, originalName: "amount") };
            await WithBuilderSchema(mysql, columns, new[] { BuilderKey("id") }, (_, builder) =>
            {
                var insert = builder.GetInsertCommand();
                var parameter = Assert.IsType<OceanBaseParameter>(insert.Parameters[1]); parameter.Value = 1.234m;
                Assert.Null(parameter.DeclaredDecimalScale);
                var mode = mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
                Assert.Equal(PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = 1.234m } }, mode), PreparedParameters.Encode(new[] { parameter }, mode));
            });
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BuilderDoesNotUseNullableUniqueOrMissingKeysForUpdateAndDelete(bool mysql, bool noIndex)
        {
            var columns = new[] { SchemaColumn("payload", 253, 80, originalName: "payload") };
            var row = noIndex ? new string?[] { "租户库", "ledger", null, null, null, null, null, null } : BuilderKey("payload", nullable: "YES", kind: "U");
            await WithBuilderSchema(mysql, columns, new[] { row }, (_, builder) =>
            {
                Assert.Single(builder.GetInsertCommand().Parameters);
                Assert.Throws<InvalidOperationException>(() => builder.GetUpdateCommand());
                Assert.Throws<InvalidOperationException>(() => builder.GetDeleteCommand());
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderRejectsUnknownQueryProvenanceEvenForInsert(bool mysql)
        {
            await WithBuilderSchema(mysql, new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id") }, Array.Empty<string?[]>(),
                (_, builder) => Assert.Throws<NotSupportedException>(() => builder.GetInsertCommand()), distinct: true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderCompositeKeysAndAutoIncrementMetadataStayDistinct(bool mysql)
        {
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 513, originalName: "id"), SchemaColumn("part", 8, 20, flags: 1, originalName: "part"), SchemaColumn("payload", 253, 80, originalName: "payload") };
            await WithBuilderSchema(mysql, columns, new[] { BuilderKey("id"), BuilderKey("part", "2") }, (_, builder) =>
            {
                var insert = builder.GetInsertCommand(); Assert.Equal(2, insert.Parameters.Count); Assert.Equal("part", insert.Parameters[0].SourceColumn);
                var delete = builder.GetDeleteCommand(); Assert.Equal(4, delete.Parameters.Count);
                Assert.Equal("id", delete.Parameters[0].SourceColumn); Assert.Equal("part", delete.Parameters[1].SourceColumn);
                foreach (DbParameter parameter in delete.Parameters) Assert.Equal(DataRowVersion.Original, parameter.SourceVersion);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderDecimalMetadataPreservesSignedScaleAndIntegerNullIndicators(bool mysql)
        {
            var scale = mysql ? 2 : -2;
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id"), SchemaColumn("amount", 246, 8, 6, unchecked((byte)scale), originalName: "amount") };
            await WithBuilderSchema(mysql, columns, new[] { BuilderKey("id") }, (_, builder) =>
            {
                var command = builder.GetUpdateCommand();
                var value = Assert.IsType<OceanBaseParameter>(command.Parameters[1]); Assert.Equal(DbType.Decimal, value.DbType); Assert.Equal(scale, value.DecimalScale); Assert.Equal((byte)6, value.Precision);
                var indicator = Assert.IsType<OceanBaseParameter>(command.Parameters[3]); Assert.True(indicator.SourceColumnNullMapping); Assert.Equal(DbType.Int32, indicator.DbType); Assert.Null(indicator.DecimalScale); Assert.Equal((byte)0, indicator.Precision);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderInheritsCallerTransactionAndRefreshesOwnedCommands(bool mysql)
        {
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id") };
            await WithBuilderSchema(mysql, columns, new[] { BuilderKey("id") }, (adapter, builder) =>
            {
                var first = builder.GetUpdateCommand(true);
                Assert.Same(adapter.SelectCommand!.Transaction, first.Transaction); Assert.NotNull(first.Transaction);
                builder.RefreshSchema();
                var second = builder.GetUpdateCommand(true); Assert.NotSame(first, second);
                Assert.Same(adapter.SelectCommand.Transaction, second.Transaction);
                Assert.Throws<ObjectDisposedException>(() => first.ExecuteNonQuery());
            }, transaction: true, snapshots: 2);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderRequiresVisibleKeysForGeneratedUpdateButAllowsInsert(bool mysql)
        {
            var payload = SchemaColumn("payload", 253, 80, originalName: "payload");
            var hidden = SchemaColumn("__ob_key_0", 8, 20, flags: 1, originalName: "id");
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await KeyPrepare(channel, 91, payload);
                await KeyDirectory(channel, mysql, BuilderKey("id"));
                await StatementCommand(channel, 22); await KeyPrepare(channel, 93, payload, hidden);
                var closed = new HashSet<byte>();
                for (var i = 0; i < 2; i++) { var close = await StatementCommand(channel, 25); closed.Add(close[1]); }
                Assert.True(closed.SetEquals(new byte[] { 91, 93 }));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var select = Assert.IsType<OceanBaseCommand>(connection.CreateCommand());
                select.CommandText = mysql ? "select payload from `租户库`.ledger" : "select payload from \"租户库\".\"ledger\"";
                using var adapter = new OceanBaseDataAdapter(select); using var builder = new OceanBaseCommandBuilder(adapter);
                Assert.Single(builder.GetInsertCommand().Parameters);
                Assert.Throws<InvalidOperationException>(() => builder.GetUpdateCommand()); Assert.Throws<InvalidOperationException>(() => builder.GetDeleteCommand());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderRejectsWrongModeQuotesBeforeMetadataAndKeepsConnectionUsable(bool mysql)
        {
            var server = Server(async channel => { await Query(channel, "after quote rejection"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var select = Assert.IsType<OceanBaseCommand>(connection.CreateCommand()); select.CommandText = "select id from ledger";
                using var adapter = new OceanBaseDataAdapter(select); using var builder = new OceanBaseCommandBuilder(adapter) { QuotePrefix = mysql ? "\"" : "`", QuoteSuffix = mysql ? "\"" : "`" };
                Assert.Throws<InvalidOperationException>(() => builder.GetInsertCommand());
                using var after = connection.CreateCommand(); after.CommandText = "after quote rejection"; Assert.Equal(1, after.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task AdapterGeneratedCrudRetainsConflictsAndRaisesRowEvents(bool mysql, bool keepChanges)
        {
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id"), SchemaColumn("payload", 253, 80, originalName: "payload") };
            var mode = mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await KeyPrepare(channel, 91, columns);
                await KeyDirectory(channel, mysql, BuilderKey("id"));
                var values = new object?[][] { new object?[] { "y", 1L, 0, "x" }, new object?[] { 2L, 1, DBNull.Value }, new object?[] { 3L, "new" } };
                for (var i = 0; i < values.Length; i++)
                {
                    var request = await StatementCommand(channel, 22); var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                    Assert.StartsWith(new[] { "UPDATE ", "DELETE FROM ", "INSERT INTO " }[i], sql);
                    Assert.Contains(mysql ? "`租户库`.`ledger`" : "\"租户库\".\"ledger\"", sql);
                    Assert.DoesNotContain("new", sql); Assert.DoesNotContain(":p", sql);
                    var id = (byte)(81 + i); await Prepared(channel, id, checked((ushort)values[i].Length));
                    var parameters = new List<OceanBaseParameter>();
                    foreach (var value in values[i]) parameters.Add(new OceanBaseParameter { Value = value });
                    Assert.Equal(PreparedParameters.Execute(id, PreparedParameters.Encode(parameters.ToArray(), mode)), await StatementCommand(channel, 23));
                    await channel.WritePacketAsync(Ok(i == 0 ? (byte)0 : (byte)1));
                }
                var closed = new HashSet<byte>();
                for (var i = 0; i < 4; i++) { var close = await StatementCommand(channel, 25); closed.Add(close[1]); }
                Assert.True(closed.SetEquals(new byte[] { 91, 81, 82, 83 }));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var select = Assert.IsType<OceanBaseCommand>(connection.CreateCommand());
                select.CommandText = mysql ? "select id, payload from `租户库`.ledger" : "select id, payload from \"租户库\".\"ledger\"";
                using var adapter = new OceanBaseDataAdapter(select) { ContinueUpdateOnError = true, AcceptChangesDuringUpdate = !keepChanges };
                using var builder = new OceanBaseCommandBuilder(adapter);
                var before = new List<StatementType>(); var after = new List<RowUpdatedEventArgs>();
                adapter.RowUpdating += (_, args) => { Assert.NotNull(args.Command); before.Add(args.StatementType); };
                adapter.RowUpdated += (_, args) => after.Add(args);
                using var table = new DataTable(); table.Columns.Add("id", typeof(long)); table.Columns.Add("payload", typeof(string));
                table.Rows.Add(1L, "x"); var deleted = table.Rows.Add(2L, DBNull.Value); table.AcceptChanges();
                table.Rows[0]["payload"] = "y"; deleted.Delete(); var added = table.Rows.Add(3L, "new");
                Assert.Equal(2, adapter.Update(table)); Assert.Equal(new[] { StatementType.Update, StatementType.Delete, StatementType.Insert }, before);
                Assert.Equal(3, after.Count); Assert.IsType<DBConcurrencyException>(after[0].Errors); Assert.Equal(0, after[0].RecordsAffected);
                Assert.Equal(DataRowState.Modified, table.Rows[0].RowState); Assert.True(table.Rows[0].HasErrors); Assert.Equal("x", table.Rows[0]["payload", DataRowVersion.Original]);
                Assert.Equal(keepChanges ? DataRowState.Deleted : DataRowState.Detached, deleted.RowState); Assert.Equal(keepChanges ? DataRowState.Added : DataRowState.Unchanged, added.RowState);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
