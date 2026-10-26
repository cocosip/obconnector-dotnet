using System;
using System.Data;
using System.IO;
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
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientCharsetOraclePrepareExecuteCarriesSelectedSqlAndParameterBytes(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "latin1", true, false, CancellationToken.None);
                var packet = await ClientCharacterSetFixture.Read(channel, true, "latin1", false, CancellationToken.None);
                var cursor = new ByteCursor(packet); Assert.Equal(0xa1, cursor.ReadUInt8());
                Assert.Equal(0u, cursor.ReadUInt32()); Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(1u, cursor.ReadUInt32());
                Assert.Equal(Convert.FromHexString("424547494E202270E922283F293B20454E443B"), cursor.ReadNullableLengthEncodedBytes());
                Assert.Equal(1u, cursor.ReadUInt32()); Assert.Equal(Convert.FromHexString("0001FD0001E9"), cursor.ReadBytes(6));
                await channel.WritePacketAsync(OracleExecuteHeader(parameters: 1, columns: 0, flags: 0, rows: false));
                await channel.WritePacketAsync(Column("p", 253)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=latin1;Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "\"pé\"";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = "é" });
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientCharsetNonUtf8StringFallbackRemainsRejectedWithDrainedState(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "gbk", false, false, CancellationToken.None);
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                await MySqlLiteralState(channel, client: "gbk", connection: "gbk", collation: "gbk_bin");
                channel.BeginCommand(); await Query(channel, "SELECT 1"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=gbk");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "UPDATE t SET x=?"; command.AllowClientSidePrepareFallback = true;
                command.Parameters.Add(new OceanBaseParameter { Value = "中" });
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Open, connection.State);
                command.Parameters.Clear(); command.CommandText = "SELECT 1";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientCharsetProcedureInOutBytesAndOutputTrustShareOneExchange(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "latin1", false, false, CancellationToken.None);
                var initialization = await StatementCommand(channel, 22);
                var sql = Encoding.ASCII.GetString(initialization, 1, initialization.Length - 1);
                Assert.StartsWith("SET @", sql); var variable = sql.Substring(4).Split(" = ")[0];
                await Prepared(channel, 80, 1);
                var request = await StatementCommand(channel, 23); Assert.Equal(new byte[] { 1, 0xe9 }, request.AsSpan(14).ToArray());
                await channel.WritePacketAsync(Ok(status: 2)); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, ClientCharsetStateSql);
                await ClientCharacterSetFixture.State(channel, "latin1", false, false, CancellationToken.None);
                request = await StatementCommand(channel, 22);
                Assert.Equal(new byte[] { 67, 65, 76, 76, 32, 96, 112, 0xe9, 96, 40 }, request.AsSpan(1, 10).ToArray());
                await Prepared(channel, 81, 0);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok(status: 2));
                channel.BeginCommand(); await Query(channel, ClientCharsetStateSql);
                await ClientCharacterSetFixture.State(channel, "latin1", false, false, CancellationToken.None);
                channel.BeginCommand(); await Query(channel, "SELECT " + variable);
                await ClientCharacterSetFixture.Values(channel, new[] { "患者😀" }, false, false, CancellationToken.None);
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=latin1");
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "pé";
                var parameter = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = "é" }; command.Parameters.Add(parameter);
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal("患者😀", parameter.Value);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientCharsetGeneratedXaControlsDoNotInjectVerificationSql(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "gbk", false, false, CancellationToken.None);
                foreach (var verb in new[] { "START", "END", "PREPARE", "COMMIT" })
                {
                    var request = await StatementCommand(channel, 3);
                    Assert.StartsWith("XA " + verb + " ", Encoding.ASCII.GetString(request, 1, request.Length - 1));
                    await channel.WritePacketAsync(Ok(status: 2));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=gbk");
            try
            {
                await connection.OpenAsync(); var xa = connection.GetXaResource();
                connection.GetOpenSession().SetClientCharacterSetVerified(false);
                var xid = new OceanBaseXid(7, new byte[] { 1, 2 }, new byte[] { 3 });
                if (asynchronous)
                { await xa.StartAsync(xid); await xa.EndAsync(xid); Assert.Equal(OceanBaseXaVote.Prepared, await xa.PrepareAsync(xid)); await xa.CommitAsync(xid); }
                else { xa.Start(xid); xa.End(xid); Assert.Equal(OceanBaseXaVote.Prepared, xa.Prepare(xid)); xa.Commit(xid); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientCharsetChangedPieceAcknowledgementFailsBeforeExecutionOrVerificationSql(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "gbk", true, false, CancellationToken.None);
                var request = await ClientCharacterSetFixture.Read(channel, true, "gbk", false, CancellationToken.None);
                Assert.Equal(22, request[0]); await Prepared(channel, 71, 1);
                request = await StatementCommand(channel, 162); Assert.Equal(3, request[7]);
                await channel.WritePacketAsync(SessionVariablesOk(new System.Collections.Generic.Dictionary<string, string> { ["character_set_client"] = "latin1" }));
                channel.BeginCommand(); await Assert.ThrowsAsync<OceanBaseConnector.Internal.Protocol.ProtocolException>(() => ReadClientCommand(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=gbk");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "INSERT INTO t VALUES(:p)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = new MemoryStream(new byte[] { 1, 2, 3 }), DbType = DbType.Binary });
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
