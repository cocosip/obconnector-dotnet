using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task LobMalformedLocatorGetterFailsTheOwningExchange(bool sequential)
        {
            var server = Server(channel => LobQueryReply(channel, 210, new byte[] { 1, 2, 3 }), lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync());
                Assert.Throws<OceanBaseException>(() => reader.GetFieldValue<OceanBaseLob>(0));
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task LobBindingCannotMoveToAnotherPhysicalSession(bool reopen)
        {
            var first = Server(channel => LobQueryReply(channel, 210, InlineLob(new byte[] { 1 })), lobLocators: true);
            var second = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }, lobLocators: true);
            using var original = new OceanBaseConnection(LobConfiguration(first.Port));
            using var other = new OceanBaseConnection(LobConfiguration(second.Port));
            try
            {
                await original.OpenAsync(); using var command = original.CreateCommand(); command.CommandText = "select lob_value";
                using var lob = (OceanBaseLob)(await command.ExecuteScalarAsync())!;
                if (reopen) { original.Close(); original.ConnectionString = LobConfiguration(second.Port); await original.OpenAsync(); }
                else await other.OpenAsync();
                var target = reopen ? original : other; using var bound = target.CreateCommand(); bound.CommandText = "select :lob from dual";
                bound.Parameters.Add(new OceanBaseParameter { ParameterName = "lob", Value = lob });
                Assert.Throws<InvalidOperationException>(() => bound.ExecuteScalar());
                if (reopen)
                {
                    Assert.Throws<InvalidOperationException>(() => lob.OpenRead());
                    Assert.Throws<InvalidOperationException>(() => lob.WriteBytes(0, new byte[] { 2 }, 0, 1));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => lob.TruncateAsync(0));
                }
                lob.Dispose(); Assert.Throws<ObjectDisposedException>(() => bound.ExecuteScalar());
                using var after = target.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { original.Close(); other.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("option")] [InlineData("capability")]
        public async Task LobParametersRequireBothOptInAndNegotiatedCapability(string missing)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }, lobLocators: missing != "capability");
            using var connection = new OceanBaseConnection(missing == "option" ? Configuration(server.Port) + ";Use Oracle Prepare Execute=true" : LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "p"; command.CommandType = CommandType.StoredProcedure;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.Blob, Direction = ParameterDirection.Output });
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void LobOptionDefaultsOffAndRequiresPrepareExecuteWhenFrozen()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=a,b;User ID=u"); Assert.False(builder.UseLobLocators);
            builder.UseLobLocators = true; Assert.Throws<ArgumentException>(() => builder.Build()); builder.UseOraclePrepareExecute = true;
            var frozen = builder.Build(); builder.UseLobLocators = false;
            Assert.True(frozen.UseLobLocators); Assert.All(frozen.Endpoints, endpoint => Assert.True(frozen.ForEndpoint(endpoint).UseLobLocators));
            Assert.True(new OceanBaseConnectionStringBuilder(frozen.SanitizedConnectionString).UseLobLocators);
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
        public void LobV2CapabilityUsesClientAttributeWithoutChangingProxyCapabilities(bool ob20, bool trace)
        {
            var caps = CapabilityFlags.ClientProtocol41 | CapabilityFlags.ClientSecureConnection | CapabilityFlags.ClientConnectAttrs;
            var packet = HandshakeResponsePacket.Build("u", Array.Empty<byte>(), "", caps, caps, 45,
                "mysql_native_password", "host", ob20, trace, requestLobLocators: true);
            var cursor = new ByteCursor(packet); cursor.Skip(32); cursor.ReadNullTerminatedAscii(); cursor.ReadLengthEncodedBytes();
            var attributes = new ByteCursor(cursor.ReadLengthEncodedBytes()); var values = new Dictionary<string, string>();
            while (attributes.HasRemaining)
                values.Add(Encoding.UTF8.GetString(attributes.ReadLengthEncodedBytes()), Encoding.UTF8.GetString(attributes.ReadLengthEncodedBytes()));
            Assert.Equal("1", values["__ob_client_attribute_capability_flag"]);
            if (ob20) Assert.Equal(trace ? "311552" : "256", values["__proxy_capability_flag"]);
            else Assert.False(values.ContainsKey("__proxy_capability_flag"));
        }

        [Theory]
        [InlineData(OceanBaseDbType.Blob, 211)] [InlineData(OceanBaseDbType.Blob, 253)]
        [InlineData(OceanBaseDbType.Clob, 210)] [InlineData(OceanBaseDbType.Clob, 253)]
        [InlineData(OceanBaseDbType.String, 210)] [InlineData(OceanBaseDbType.String, 211)]
        public void LobOutputRejectsMismatchedMetadataBeforePublishing(OceanBaseDbType declared, byte actual)
        {
            var parameters = new OceanBaseParameterCollection();
            var output = new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = declared, Direction = ParameterDirection.Output, Value = "original" };
            parameters.Add(output); var plan = OracleCommandPlan.Create("proc", CommandType.StoredProcedure, parameters);
            var column = ResultColumn.Parse(Column("p", actual), OceanBaseCompatibilityMode.Oracle);
            Assert.Throws<ProtocolException>(() => plan.CaptureOutput(new[] { column }, new byte[]?[] { new byte[] { 1 } }));
            Assert.Equal("original", output.Value);
        }
    }
}
