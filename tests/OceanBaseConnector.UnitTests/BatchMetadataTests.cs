using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task DataSourceBatchReopensForEachExecutorAndReturnsPooledSessions(bool asynchronous, bool mysql, bool pooling)
        {
            var report = SessionVariablesOk(new OceanBaseConnector.Internal.Protocol.NationalCharacterSet("utf8mb4", "utf8mb4", "utf8mb4").Variables("+00:00"));
            if (mysql) report[3] &= 251;
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: !mysql, sessionStateOk: report);
            var resets = 0; var probes = 0;
            server.BeforeResetReply = () => { Interlocked.Increment(ref resets); return Task.CompletedTask; };
            server.BeforeProbeReply = () => { Interlocked.Increment(ref probes); return Task.CompletedTask; };
            await using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port) + ";Pooling=" + pooling + ";Max Pool Size=1").Build();
            await using var batch = source.CreateBatch(); BatchItem(batch, "one");
            for (var run = 0; run < 2; run++)
            {
                Assert.Equal(0, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
            }
            Assert.Equal(pooling ? 1 : 4, server.Accepted); Assert.Equal(pooling ? 1 : 4, server.Authentications.Count);
            Assert.Equal(pooling ? 4 : 0, resets); Assert.Equal(pooling ? 3 : 0, probes);
        }

        [Theory]
        [InlineData(false, "nonquery")]
        [InlineData(true, "nonquery")]
        [InlineData(false, "scalar")]
        [InlineData(true, "scalar")]
        [InlineData(false, "reader")]
        [InlineData(true, "reader")]
        public async Task DataSourceBatchCreatesEntriesAndClosesOwnedConnectionForAllExecutors(bool asynchronous, string executor)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "one");
                if (executor == "scalar") await BatchTextResult(channel, "value"); else await channel.WritePacketAsync(Ok(2));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            await using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port) + ";Command Timeout=17").Build();
            await using var batch = source.CreateBatch();
            try
            {
                Assert.Equal(17, batch.Timeout); Assert.Throws<NotSupportedException>(() => batch.Connection);
                Assert.Throws<NotSupportedException>(() => batch.Connection = null); Assert.Throws<NotSupportedException>(() => batch.Transaction);
                Assert.Throws<NotSupportedException>(() => batch.Transaction = null); Assert.Throws<NotSupportedException>(() => batch.Prepare());
                await Assert.ThrowsAsync<NotSupportedException>(() => batch.PrepareAsync());
                var item = BatchItem(batch, "one"); Assert.IsType<OceanBaseParameter>(item.CreateParameter());
                if (executor == "nonquery") Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                else if (executor == "scalar") Assert.Equal("value", asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                else
                {
                    using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                    Assert.Equal(2, reader.RecordsAffected); Assert.Throws<InvalidOperationException>(() => batch.BatchCommands.Add(batch.CreateBatchCommand()));
                    if (asynchronous) await reader.CloseAsync(); else reader.Close();
                }
                // The owned connection must close before disposing the batch itself.
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                if (asynchronous) await batch.DisposeAsync(); else batch.Dispose();
                Assert.Throws<ObjectDisposedException>(() => batch.CreateBatchCommand());
            }
            finally { server.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DataSourceBatchMetadataValidationFailureClosesOwnedConnection(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            await using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).Build();
            await using var batch = source.CreateBatch();
            try
            {
                var item = BatchItem(batch, "select :p"); item.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, DbType = DbType.Int32 });
                if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly));
                else Assert.Throws<NotSupportedException>(() => batch.ExecuteReader(CommandBehavior.SchemaOnly));
                batch.Timeout = 2;
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { server.Stop(); }
        }

        [Fact]
        public async Task DataSourceBatchPreCanceledOpenPreservesTokenAndRemainsMutable()
        {
            await using var source = new OceanBaseDataSourceBuilder("Server=unused.invalid;User ID=u;TLS Mode=Disabled").Build();
            await using var batch = source.CreateBatch(); BatchItem(batch, "select 1");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken); batch.Timeout = 2;
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BatchSchemaOnlyKeepsReservationAndReusesEachMetadataWithoutReadingInputs(bool asynchronous, bool mysql)
        {
            var server = Server(async channel =>
            {
                for (byte id = 61; id < 63; id++)
                {
                    var prepare = await StatementCommand(channel, 22);
                    Assert.Equal(id == 61 ? "select ?, ?" : "select side_effect() for update", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                    await Prepared(channel, id, id == 61 ? (ushort)2 : (ushort)0, 1);
                }
                // The second batch run reuses metadata; neither run executes or uploads inputs.
                channel.BeginCommand(); await Query(channel, "after metadata"); await channel.WritePacketAsync(Ok(1));
                for (byte id = 61; id < 63; id++) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var batch = connection.CreateBatch(); using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
                var first = BatchItem(batch, mysql ? "select ?, ?" : "select :a, :b");
                first.Parameters.Add(new OceanBaseParameter { ParameterName = "a", Value = Guid.NewGuid() });
                first.Parameters.Add(new OceanBaseParameter { ParameterName = "b", Value = stream });
                var second = BatchItem(batch, "select side_effect() for update");
                for (var run = 0; run < 2; run++)
                {
                    using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.SequentialAccess)
                        : batch.ExecuteReader(CommandBehavior.SchemaOnly | CommandBehavior.SequentialAccess);
                    for (var result = 0; result < 2; result++)
                    {
                        Assert.Equal(1, reader.FieldCount); Assert.Equal(typeof(long), reader.GetFieldType(0));
                        Assert.Single(reader.GetColumnSchema()); using var table = reader.GetSchemaTable(); Assert.Single(table!.Rows);
                        Assert.False(reader.HasRows); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                        Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
                        Assert.Equal(result == 0, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    }
                    Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
                    Assert.Equal(-1, reader.RecordsAffected); Assert.Equal(-1, first.RecordsAffected); Assert.Equal(-1, second.RecordsAffected);
                }
                Assert.Equal(0, stream.Position); Assert.True(stream.CanRead);
                using var after = connection.CreateCommand(); after.CommandText = "after metadata"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
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
        public async Task BatchKeyInfoUsesReservedSessionAndExposesHiddenKeysForEachResult(bool asynchronous, bool mysql, bool schemaOnly)
        {
            var source = mysql ? "`租户库`.ledger" : "\"租户库\".\"ledger\"";
            var sql = "select l.payload from " + source + " l";
            var columns = new[] { SchemaColumn("payload", 253, 80, originalName: "payload"), SchemaColumn("__ob_key_0", 8, 20, flags: 1, originalName: "id") };
            var server = Server(async channel =>
            {
                for (byte id = 71; id < 75; id += 2)
                {
                    var prepare = await StatementCommand(channel, 22); Assert.Equal(sql, Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                    await KeyPrepare(channel, id, columns[0]);
                    await KeyDirectory(channel, mysql, new string?[] { "租户库", "ledger", "PK", "P", "id", "1", "NO", "YES" });
                    var rewritten = await StatementCommand(channel, 22); var quote = mysql ? "`" : "\"";
                    Assert.Contains("l." + quote + "id" + quote + " AS " + quote + "__ob_key_0" + quote, Encoding.UTF8.GetString(rewritten, 1, rewritten.Length - 1));
                    await KeyPrepare(channel, (byte)(id + 1), columns);
                    if (!schemaOnly)
                    {
                        Assert.Equal(OceanBaseConnector.Internal.Protocol.PreparedParameters.Execute((uint)(id + 1), Array.Empty<byte>()), await StatementCommand(channel, 23));
                        await channel.WritePacketAsync(new byte[] { 2 });
                        foreach (var column in columns) await channel.WritePacketAsync(column);
                        await channel.WritePacketAsync(Eof());
                        await channel.WritePacketAsync(new byte[] { 0, 0, 1, 120, id, 0, 0, 0, 0, 0, 0, 0 });
                        await channel.WritePacketAsync(Eof());
                    }
                    Assert.Equal(new byte[] { 25, (byte)(id + 1), 0, 0, 0 }, await StatementCommand(channel, 25));
                }
                for (byte id = 71; id < 75; id += 2) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var batch = connection.CreateBatch(); var first = BatchItem(batch, sql); var second = BatchItem(batch, sql);
                var behavior = CommandBehavior.KeyInfo | CommandBehavior.SequentialAccess | (schemaOnly ? CommandBehavior.SchemaOnly : 0);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                for (var result = 0; result < 2; result++)
                {
                    Assert.Equal(2, reader.FieldCount); Assert.Equal(1, reader.VisibleFieldCount);
                    var metadata = reader.GetColumnSchema(); Assert.False(metadata[0].IsHidden); Assert.False(metadata[0].IsKey);
                    Assert.True(metadata[1].IsKey); Assert.True(metadata[1].IsHidden); Assert.True(metadata[1].IsUnique);
                    using var table = reader.GetSchemaTable(); Assert.Equal(true, table!.Rows[1]["IsHidden"]);
                    Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
                    if (!schemaOnly)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("x", reader.GetString(0)); Assert.Equal(71 + result * 2, reader.GetInt64(1));
                    }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(result == 0, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
                Assert.Equal(sql, first.CommandText); Assert.Equal(sql, second.CommandText);
                Assert.Equal(-1, first.RecordsAffected); Assert.Equal(-1, second.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BatchMetadataEarlyCloseSkipsSuffixAndHonorsCloseConnection(bool asynchronous, bool closeConnection)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 63, 0, 1);
                if (closeConnection) Assert.Equal(0, await channelStreamRead(channel));
                else
                {
                    channel.BeginCommand(); await Query(channel, "after early close"); await channel.WritePacketAsync(Ok(1));
                    Assert.Equal(new byte[] { 25, 63, 0, 0, 0 }, await StatementCommand(channel, 25));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                BatchItem(batch, "select first_metadata"); var suffix = BatchItem(batch, "select never");
                var behavior = CommandBehavior.SchemaOnly | (closeConnection ? CommandBehavior.CloseConnection : 0);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                Assert.Equal(1, reader.FieldCount); Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.True(reader.IsClosed); Assert.Equal(-1, suffix.RecordsAffected);
                Assert.Equal(closeConnection ? ConnectionState.Closed : ConnectionState.Open, connection.State);
                batch.Timeout = 7;
                if (!closeConnection)
                {
                    using var after = connection.CreateCommand(); after.CommandText = "after early close"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchMetadataOldReaderCannotCloseReopenedLease(bool asynchronous)
        {
            var first = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 64, 0, 1);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            var second = Server(async channel => { await Query(channel, "after reopen"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "select metadata");
                using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.CloseConnection);
                connection.Close(); connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync();
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after reopen"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
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
        public async Task BatchMetadataLaterFailureAttributesItemAndReleasesReservation(bool asynchronous, bool mysql, bool catalog)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 65, 0, 1);
                await StatementCommand(channel, 22);
                if (catalog)
                {
                    await KeyPrepare(channel, 66, SchemaColumn("id", 8, 20, flags: 1, originalName: "id"));
                    var directory = await StatementCommand(channel, 22);
                    Assert.Contains(mysql ? "INFORMATION_SCHEMA.STATISTICS" : "ALL_INDEXES", Encoding.UTF8.GetString(directory, 1, directory.Length - 1));
                }
                await channel.WritePacketAsync(Error());
                channel.BeginCommand(); await Query(channel, "after failure"); await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 25, 65, 0, 0, 0 }, await StatementCommand(channel, 25));
                if (catalog) Assert.Equal(new byte[] { 25, 66, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var batch = connection.CreateBatch(); var first = BatchItem(batch, "select 1");
                var bad = BatchItem(batch, mysql ? "select id from `租户库`.ledger" : "select id from \"租户库\".\"ledger\"");
                var suffix = BatchItem(batch, "select never");
                var behavior = CommandBehavior.SchemaOnly | (catalog ? CommandBehavior.KeyInfo : 0);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => reader.NextResultAsync())
                    : Assert.Throws<OceanBaseException>(() => reader.NextResult());
                Assert.Same(bad, error.BatchCommand); Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.True(reader.IsClosed); Assert.Equal(-1, first.RecordsAffected); Assert.Equal(-1, bad.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected);
                batch.Timeout = 8;
                using var after = connection.CreateCommand(); after.CommandText = "after failure"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BatchMetadataLaterCancellationDoesNotBecomeUnknownSql(bool catalog, bool timeout)
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 67, 0, 1);
                await StatementCommand(channel, 22);
                if (catalog)
                {
                    await KeyPrepare(channel, 68, SchemaColumn("id", 8, 20, flags: 1, originalName: "id"));
                    await StatementCommand(channel, 22);
                }
                sent.SetResult(); Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); batch.Timeout = timeout ? 1 : 0;
                BatchItem(batch, "select 1"); BatchItem(batch, "select id from \"租户库\".\"ledger\""); var suffix = BatchItem(batch, "select never");
                var behavior = CommandBehavior.SchemaOnly | (catalog ? CommandBehavior.KeyInfo : 0);
                using var reader = await batch.ExecuteReaderAsync(behavior);
                var next = reader.NextResultAsync(cancellation.Token); await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => next.WaitAsync(TimeSpan.FromSeconds(10)));
                else
                {
                    cancellation.Cancel(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                }
                Assert.True(reader.IsClosed); Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal(-1, suffix.RecordsAffected);
                batch.Timeout = 9;
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BatchMetadataRejectsOutputsProceduresAndMultiStatementsWithoutSending(bool asynchronous, bool keyInfo)
        {
            var server = Server(async channel => { await Query(channel, "after validation"); await channel.WritePacketAsync(Ok(1)); }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true;Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var item = BatchItem(batch, "select :a");
                var output = new OceanBaseParameter { ParameterName = "a", Direction = ParameterDirection.Output, DbType = DbType.Int32, Value = 42 };
                item.Parameters.Add(output);
                var behavior = keyInfo ? CommandBehavior.KeyInfo : CommandBehavior.SchemaOnly;
                async Task Rejected()
                {
                    if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteReaderAsync(behavior));
                    else Assert.Throws<NotSupportedException>(() => batch.ExecuteReader(behavior));
                    batch.Timeout = 10;
                }
                await Rejected(); Assert.Equal(42, output.Value); item.Parameters.Clear();
                item.CommandType = CommandType.StoredProcedure; item.CommandText = "proc"; await Rejected();
                item.CommandType = CommandType.Text; item.CommandText = "select 1; select 2"; await Rejected();
                using var after = connection.CreateCommand(); after.CommandText = "after validation"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
