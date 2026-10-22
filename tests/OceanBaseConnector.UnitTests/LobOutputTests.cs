using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> LobOutputCases()
        {
            foreach (var direction in new[] { ParameterDirection.Output, ParameterDirection.InputOutput, ParameterDirection.ReturnValue })
                foreach (var character in new[] { false, true })
                    foreach (var kind in new[] { "value", "null", "error" }) yield return new object[] { direction, character, kind };
        }

        [Theory]
        [MemberData(nameof(LobOutputCases))]
        public async Task LobOutputsPublishAtomicallyAndBindOriginalLocators(ParameterDirection direction, bool character, string kind)
        {
            var type = character ? (byte)211 : (byte)210; var locator = InlineLob(Encoding.UTF8.GetBytes("患者😀"));
            var server = Server(async channel =>
            {
                if (direction == ParameterDirection.InputOutput) await LobQueryReply(channel, type, locator);
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.Skip(10);
                Assert.Equal(direction == ParameterDirection.ReturnValue ? "BEGIN ? := p(); END;" : "BEGIN p(?); END;", Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
                Assert.Equal(1u, request.ReadUInt32()); Assert.Equal(direction == ParameterDirection.InputOutput ? 0 : 1, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
                Assert.Equal(direction == ParameterDirection.InputOutput ? (ushort)type : (ushort)6, request.ReadUInt16());
                if (direction == ParameterDirection.InputOutput) Assert.Equal(locator, request.ReadLengthEncodedBytes());
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, 4)); await channel.WritePacketAsync(Column("p", type)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("p", type)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(kind == "null" ? new byte[] { 0, 4 } : new byte[] { 0, 0 }.Concat(WithLength(locator)).ToArray());
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(kind == "error" ? Error() : Ok());
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                OceanBaseLob? input = null;
                if (direction == ParameterDirection.InputOutput) { command.CommandText = "select lob_value"; input = (OceanBaseLob)(await command.ExecuteScalarAsync())!; }
                using (input)
                {
                    command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                    var output = new OceanBaseParameter { ParameterName = "p", Direction = direction, Value = input ?? (object)"old" };
                    // InputOutput must infer the same provider type on both the request and response.
                    if (direction != ParameterDirection.InputOutput) output.OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
                    command.Parameters.Add(output);
                    if (kind == "error") { await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync()); Assert.Same(input ?? (object)"old", output.Value); }
                    else
                    {
                        await command.ExecuteNonQueryAsync();
                        if (kind == "null") Assert.Same(DBNull.Value, output.Value);
                        else
                        {
                            using var value = Assert.IsType<OceanBaseLob>(output.Value); Assert.Equal(character, value.IsCharacter);
                            if (character) { using var source = value.OpenTextReader(); Assert.Equal("患者😀", await source.ReadToEndAsync()); }
                            else { using var source = value.OpenRead(); using var target = new MemoryStream(); await source.CopyToAsync(target); Assert.Equal(Encoding.UTF8.GetBytes("患者😀"), target.ToArray()); }
                        }
                    }
                }
                command.Dispose(); using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
