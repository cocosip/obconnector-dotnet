using System;
using System.Data;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false, 2001)]
        [InlineData(false, true, 2001)]
        [InlineData(true, false, 2001)]
        [InlineData(true, true, 2001)]
        [InlineData(false, false, 65536)]
        [InlineData(false, true, 65536)]
        [InlineData(true, false, 65536)]
        [InlineData(true, true, 65536)]
        public async Task OracleLargeBinaryLiteralsUseTextAndPreserveSnapshots(bool fallback, bool asynchronous, int size)
        {
            var bytes = new byte[size]; for (var i = 0; i < size; i++) bytes[i] = (byte)i;
            var literal = "(HEXTORAW('" + Convert.ToHexString(bytes) + "'))";
            OceanBaseParameter? input = null;
            var server = Server(async channel =>
            {
                if (fallback)
                {
                    await StatementCommand(channel, 22);
                    bytes[0] = 99; input!.Value = new byte[] { 99 }; input.DbType = DbType.String;
                    await channel.WritePacketAsync(UnsupportedPrepare()); channel.BeginCommand();
                }
                await Query(channel, "UPDATE t SET v=" + literal + (fallback ? "" : "; UPDATE t SET v=" + literal));
                await channel.WritePacketAsync(Ok(2, fallback ? (byte)2 : (byte)10));
                if (!fallback) await channel.WritePacketAsync(Ok(3));
                channel.BeginCommand(); await Query(channel, "after large binary"); await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = fallback;
                command.CommandText = "UPDATE t SET v=:p" + (fallback ? "" : "; UPDATE t SET v=:P");
                input = new OceanBaseParameter { ParameterName = "p", Value = bytes }; command.Parameters.Add(input);
                Assert.Equal(fallback ? 2 : 5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
                command.Parameters.Clear(); command.CommandText = "after large binary";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
