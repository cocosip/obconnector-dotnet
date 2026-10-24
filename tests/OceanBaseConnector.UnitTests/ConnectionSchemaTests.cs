using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static readonly string[] TableDirectoryNames = { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE" };
        private static readonly string[] ColumnDirectoryNames = { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "COLUMN_DEFAULT", "IS_NULLABLE", "DATA_TYPE", "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION" };

        private static byte[][] DirectoryPackets(bool columns, bool mysql, bool binary = false, string ordinal = "1", bool nativeNumbers = false)
        {
            var names = columns ? ColumnDirectoryNames : TableDirectoryNames;
            string?[] values = columns
                ? new[] { mysql ? "Db目标" : null, mysql ? null : "Owner目标", "Mixed_Table", "Mixed_Column", ordinal, "'Mixed'", "YES", "VARCHAR", "4294967295", null, "-2", null }
                : new[] { mysql ? "Db目标" : null, mysql ? null : "Owner目标", "Mixed_Table", "TABLE" };
            using var row = new MemoryStream();
            if (binary)
            {
                row.WriteByte(0); var nulls = new byte[(names.Length + 9) / 8];
                for (var i = 0; i < names.Length; i++) if (values[i] == null) nulls[(i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
                row.Write(nulls);
            }
            foreach (var value in values)
            {
                if (value == null) { if (!binary) row.WriteByte(251); continue; }
                var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes);
            }
            return new[] { new[] { (byte)names.Length } }.Concat(names.Select((x, i) => Column(x, nativeNumbers && (i == 4 || i == 8 || i == 10) ? (byte)246 : (byte)253)))
                .Concat(new[] { Eof(), row.ToArray(), Eof() }).ToArray();
        }

        [Fact]
        public async Task ConnectionSchemaStaticCollectionsHaveFixedShapesAndNoNetworkIo()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
            try
            {
                await connection.OpenAsync(); var transport = new ProtocolFaultStream(Array.Empty<byte>()); InstallFaultStream(connection, transport);
                var collections = connection.GetSchema();
                Assert.Equal("MetaDataCollections", collections.TableName); Assert.Equal(19, collections.Rows.Count);
                Assert.Equal(new[] { "CollectionName", "NumberOfRestrictions", "NumberOfIdentifierParts" }, collections.Columns.Cast<DataColumn>().Select(x => x.ColumnName));
                Assert.Equal(new[] { 0, 0, 4, 4, 5, 5, 3, 4, 5, 5, 5, 5, 4, 5, 4, 3, 2, 3, 0 }, collections.Rows.Cast<DataRow>().Select(x => (int)x[1]));
                collections.Rows.Clear(); Assert.Equal(19, (await connection.GetSchemaAsync()).Rows.Count);
                var information = await connection.GetSchemaAsync("datasourceinformation");
                Assert.Equal(17, information.Columns.Count); var row = Assert.Single(information.Rows.Cast<DataRow>());
                Assert.Equal("OceanBase", row["DataSourceProductName"]); Assert.Equal(connection.ServerVersion, row["DataSourceProductVersion"]);
                Assert.Equal(DBNull.Value, row["DataSourceProductVersionNormalized"]); Assert.Equal(":{0}", row["ParameterMarkerFormat"]);
                Assert.Matches((string)row["ParameterNamePattern"], "a_$#1");
                Assert.DoesNotMatch("^(?:" + row["ParameterNamePattern"] + ")$", "目标");
                Assert.Equal(typeof(GroupByBehavior), information.Columns["GroupByBehavior"]!.DataType);
                Assert.Equal(CultureInfo.InvariantCulture, information.Locale);
                Assert.Throws<NotSupportedException>(() => connection.GetSchema("UnknownCollection"));
                Assert.Throws<ArgumentNullException>(() => connection.GetSchema(null!));
                Assert.Throws<ArgumentException>(() => connection.GetSchema("Tables", new string[5]));
                Assert.Throws<ArgumentException>(() => connection.GetSchema("MetaDataCollections", new string[1]));
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetSchemaAsync(canceled.Token));
                Assert.Empty(transport.Written); Assert.Equal(0, transport.SyncCalls + transport.AsyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task ConnectionSchemaDirectoriesUseModeDictionaryAndStablePublicTypes(bool asynchronous, bool mysql, bool columns)
        {
            var server = Server(async channel =>
            {
                var request = await StatementCommand(channel, 3); var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                Assert.Contains(mysql ? columns ? "INFORMATION_SCHEMA.COLUMNS" : "INFORMATION_SCHEMA.TABLES" : columns ? "ALL_TAB_COLUMNS" : "ALL_OBJECTS", sql);
                foreach (var packet in DirectoryPackets(columns, mysql)) await channel.WritePacketAsync(packet);
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var table = asynchronous ? await connection.GetSchemaAsync(columns ? "Columns" : "Tables", null!) : connection.GetSchema(columns ? "Columns" : "Tables", null!);
                Assert.Equal(columns ? ColumnDirectoryNames : TableDirectoryNames, table.Columns.Cast<DataColumn>().Select(x => x.ColumnName));
                var row = Assert.Single(table.Rows.Cast<DataRow>());
                Assert.Equal(mysql ? "Db目标" : DBNull.Value, row[0]); Assert.Equal(mysql ? DBNull.Value : "Owner目标", row[1]);
                Assert.Equal("Mixed_Table", row[2]);
                if (columns)
                {
                    Assert.Equal(1, row["ORDINAL_POSITION"]); Assert.Equal(4294967295L, row["CHARACTER_MAXIMUM_LENGTH"]);
                    Assert.Equal(-2, row["NUMERIC_SCALE"]); Assert.Equal(DBNull.Value, row["NUMERIC_PRECISION"]);
                    Assert.Equal("'Mixed'", row["COLUMN_DEFAULT"]);
                }
                else Assert.Equal("TABLE", row["TABLE_TYPE"]);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ConnectionSchemaRestrictionsAreExactProtocolParameters(bool asynchronous, bool mysql)
        {
            const string name = "Mixed_%' OR 1=1 --\\\"目标";
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22); var sql = Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1);
                Assert.DoesNotContain(name, sql); Assert.DoesNotContain("LIKE", sql); Assert.Contains(mysql ? "TABLE_SCHEMA = ?" : "OWNER = ?", sql);
                await Prepared(channel, 71, 3);
                var execute = await StatementCommand(channel, 23);
                var expected = PreparedParameters.Execute(71, PreparedParameters.Encode(new[] {
                    new OceanBaseParameter { Value = "Db目标" }, new OceanBaseParameter { Value = name }, new OceanBaseParameter { Value = "TABLE" }
                }, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle));
                Assert.Equal(expected, execute);
                foreach (var packet in DirectoryPackets(false, mysql, true)) await channel.WritePacketAsync(packet);
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var restrictions = mysql ? new[] { "Db目标", null, name, "TABLE" } : new[] { null, "Db目标", name, "TABLE" };
                var table = asynchronous ? await connection.GetSchemaAsync("Tables", restrictions) : connection.GetSchema("Tables", restrictions);
                Assert.Single(table.Rows.Cast<DataRow>()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionSchemaStaticAndAbsentPartsRespectAmbientAndBusyGuards(bool mysql)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(Array.Empty<byte>()); InstallFaultStream(connection, transport);
                foreach (var collection in new[] { "MetaDataCollections", "DataSourceInformation", "Tables", "Columns" })
                {
                    using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                    {
                        Assert.Throws<InvalidOperationException>(() => connection.GetSchema(collection));
                        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.GetSchemaAsync(collection));
                    }
                    using (var lease = connection.GetOpenSession().Acquire())
                    {
                        Assert.Throws<InvalidOperationException>(() => connection.GetSchema(collection));
                        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.GetSchemaAsync(collection));
                        lease.CompleteIfActive();
                    }
                }
                foreach (var value in new[] { "", "%", "ignored" })
                {
                    var restrictions = mysql ? new[] { null, value } : new[] { value };
                    Assert.Empty(connection.GetSchema("Tables", restrictions).Rows.Cast<DataRow>());
                    Assert.Empty((await connection.GetSchemaAsync("Columns", restrictions)).Rows.Cast<DataRow>());
                }
                var information = connection.GetSchema("DataSourceInformation");
                Assert.Equal(mysql ? "?" : ":{0}", information.Rows[0]["ParameterMarkerFormat"]);
                Assert.Empty(transport.Written);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionSchemaBindsCurrentLocalTransactionWithoutCompletingIt(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await StatementCommand(channel, 3);
                foreach (var packet in DirectoryPackets(false, false)) await channel.WritePacketAsync(packet);
                channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                var table = asynchronous ? await connection.GetSchemaAsync("Tables") : connection.GetSchema("Tables");
                Assert.Single(table.Rows.Cast<DataRow>()); Assert.Same(connection, transaction.Connection);
                using var unbound = connection.CreateCommand(); unbound.CommandText = "not bound"; Assert.Throws<InvalidOperationException>(() => unbound.ExecuteNonQuery());
                await transaction.RollbackAsync(); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ConnectionSchemaPermissionErrorsStayKnownAndDoNotRetry(bool asynchronous, bool mysql)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 3); await channel.WritePacketAsync(new byte[] { 255, 119, 4, 35, 52, 50, 48, 48, 48 });
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.GetSchemaAsync("Columns") : Task.FromResult(connection.GetSchema("Columns")));
                Assert.Equal(1143, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Open, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("token")]
        [InlineData("timeout")]
        [InlineData("close")]
        public async Task ConnectionSchemaAsyncWaitUsesCommandLifetimeAndDoesNotBlock(string origin)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel => { await StatementCommand(channel, 3); received.TrySetResult(); Assert.Equal(0, await channelStreamRead(channel)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=" + (origin == "timeout" ? 1 : 10));
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync();
                var pending = connection.GetSchemaAsync("Tables", cancellation.Token);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                if (origin == "token") cancellation.Cancel(); else if (origin == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (origin == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                if (origin == "token") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(origin == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionSchemaUsesOnlyRequestedTransportAndHandlesEmptyResults(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var packets = DirectoryPackets(false, false).Where((_, i) => i != 6).ToArray();
                using var input = new MemoryStream(); byte sequence = 1;
                foreach (var packet in packets) input.Write(ResponseFrame(packet, sequence++));
                var transport = new ProtocolFaultStream(input.ToArray()); InstallFaultStream(connection, transport);
                var table = asynchronous ? await connection.GetSchemaAsync("Tables") : connection.GetSchema("Tables");
                Assert.Equal(4, table.Columns.Count); Assert.Empty(table.Rows.Cast<DataRow>()); Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(0, asynchronous ? transport.SyncCalls : transport.AsyncCalls);
                Assert.True((asynchronous ? transport.AsyncCalls : transport.SyncCalls) > 0);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionSchemaPartialWriteDestroysSessionWithoutRetry(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var transport = new ProtocolFaultStream(Array.Empty<byte>(), 6); InstallFaultStream(connection, transport);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.GetSchemaAsync("Tables") : Task.FromResult(connection.GetSchema("Tables")));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal(6, transport.Written.Length);
                Assert.Equal(0, asynchronous ? transport.SyncCalls : transport.AsyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
