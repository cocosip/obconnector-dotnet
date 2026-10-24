using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        public async Task SchemaOnlyBindsNamedAndSourceResolvedComplexMetadata(bool asynchronous, int sourceKind)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "TypeOwner");
                await SourceComplexPrepare(channel, sourceKind == 0 ? ComplexColumn("TypeOwner") : SourceComplexColumn(typeName: sourceKind == 2 ? "" : "Thing"));
                if (sourceKind != 0)
                {
                    await SourceTypeLookup(channel, rows: new[] { new string?[] { "TypeOwner", "Thing" } });
                    Assert.Equal(new byte[] { 25, 182, 0, 0, 0 }, await StatementCommand(channel, 25));
                }
                Assert.Equal(new byte[] { 25, 181, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("TypeOwner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "select complex_value";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly) : command.ExecuteReader(CommandBehavior.SchemaOnly);
                Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(0));
                Assert.Equal("Thing", reader.GetDataTypeName(0));
                Assert.Equal((int)OceanBaseDbType.Array, reader.GetColumnSchema()[0]["ProviderType"]);
                Assert.False(reader.Read());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SchemaOnlyDoesNotValidateDisposedLobInputValue(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, InlineLob(new byte[] { 7 }));
                await StatementCommand(channel, 22); await Prepared(channel, 46, 1, 1);
                Assert.Equal(new byte[] { 25, 46, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                OceanBaseLob lob;
                using (var rows = await source.ExecuteReaderAsync())
                { Assert.True(await rows.ReadAsync()); lob = rows.GetFieldValue<OceanBaseLob>(0); Assert.False(await rows.ReadAsync()); }
                lob.Dispose();
                using var command = connection.CreateCommand(); command.CommandText = "select :lob";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "lob", Value = lob });
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly) : command.ExecuteReader(CommandBehavior.SchemaOnly);
                Assert.Equal(typeof(long), reader.GetFieldType(0)); Assert.False(reader.Read());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SchemaOnlyIgnoresParameterValuesAndReusesPreparedMetadata(bool asynchronous, bool mysql)
        {
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22);
                Assert.Equal("select ?, ?", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 43, 2, 1);
                // Two schema reads and explicit Prepare must reuse the same statement, without execute/upload.
                Assert.Equal(new byte[] { 25, 43, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
                using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = mysql ? "select ?, ?" : "select :a, :b";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "a", Value = Guid.NewGuid() });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "b", Value = stream });
                for (var i = 0; i < 2; i++)
                {
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly) : command.ExecuteReader(CommandBehavior.SchemaOnly);
                    Assert.Equal(typeof(long), reader.GetFieldType(0)); Assert.False(reader.Read());
                }
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(0, stream.Position); Assert.True(stream.CanRead); Assert.False(command.HasSentSql);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SchemaOnlyDoesNotFallbackWhenServerRejectsPreparation(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48 });
                channel.BeginCommand(); await Query(channel, "after rejected prepare"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "show unsupported"; command.AllowClientSidePrepareFallback = true;
                var error = asynchronous
                    ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteReaderAsync(CommandBehavior.SchemaOnly))
                    : Assert.Throws<OceanBaseException>(() => command.ExecuteReader(CommandBehavior.SchemaOnly));
                Assert.Equal(1295, error.ServerErrorCode); Assert.Equal("HY000", error.SqlState);
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.False(command.HasSentSql);
                command.CommandText = "after rejected prepare"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SchemaOnlyRejectsMultiStatementsAndOutputsBeforeSend(bool asynchronous)
        {
            var server = Server(async channel => { await Query(channel, "after rejected schema"); await channel.WritePacketAsync(Ok(1)); }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true;Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select 1; select 2";
                async Task Rejected()
                {
                    if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync(CommandBehavior.SchemaOnly));
                    else Assert.Throws<NotSupportedException>(() => command.ExecuteReader(CommandBehavior.SchemaOnly));
                }
                await Rejected(); command.CommandText = "select :a";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "a", Direction = ParameterDirection.Output, DbType = DbType.Int32 });
                await Rejected(); command.Parameters.Clear(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "proc";
                await Rejected(); command.CommandType = CommandType.Text; command.CommandText = "after rejected schema";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SchemaOnlyCloseConnectionAndLobComplexMetadataNeedNoExecution(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 44, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(Column("lob", 210));
                await channel.WritePacketAsync(ComplexColumn(schema: "", name: ""));
                await channel.WritePacketAsync(Eof());
                Assert.Equal(new byte[] { 25, 44, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob, complex";
                var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.CloseConnection)
                    : command.ExecuteReader(CommandBehavior.SchemaOnly | CommandBehavior.CloseConnection);
                Assert.Equal(typeof(OceanBaseLob), reader.GetFieldType(0)); Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(1));
                Assert.False(reader.Read());
                // Statement disposal can run while the terminal metadata reader remains open.
                command.Dispose();
                if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SchemaOnlyCancellationDuringPreparePreservesTokenWithoutUnknownExecution(bool timeout)
        {
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel => { await StatementCommand(channel, 22); sent.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "select slow_metadata";
                command.CommandTimeout = timeout ? 1 : 0;
                var operation = command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellation.Token);
                await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => operation);
                else
                {
                    cancellation.Cancel(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                }
                Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
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
        public async Task SchemaOnlyPreparesTargetWithoutExecutingFunctionsOrFetchingRows(bool asynchronous, bool mysql, bool deprecated)
        {
            const string sql = "select side_effect() for update";
            var server = Server(async channel =>
            {
                var request = await StatementCommand(channel, 22, deprecated);
                Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 41, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(Column("id"));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                // Any target COM_QUERY/EXECUTE/FETCH instead of this next query fails the test.
                channel.BeginCommand();
                await Query(channel, "after metadata", deprecated);
                await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 25, 41, 0, 0, 0 }, await StatementCommand(channel, 25, deprecated));
            }, deprecateEof: deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                if (mysql) UseInternalMySqlFixture(connection);
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = sql; command.FetchSize = 17;
                command.AllowClientSidePrepareFallback = true;
                using (var reader = asynchronous
                    ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.SequentialAccess)
                    : command.ExecuteReader(CommandBehavior.SchemaOnly | CommandBehavior.SequentialAccess))
                {
                    Assert.Equal(1, reader.FieldCount); Assert.Equal("id", reader.GetName(0));
                    Assert.Equal(typeof(int), reader.GetFieldType(0)); Assert.Single(reader.GetColumnSchema());
                    Assert.False(reader.HasRows); Assert.Equal(-1, reader.RecordsAffected);
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
                using var after = connection.CreateCommand(); after.CommandText = "after metadata";
                Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
