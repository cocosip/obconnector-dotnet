using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class ServerIdentity
    {
        internal const string OracleQuery = "select ob_version(), @@ob_compatibility_mode from dual";
        internal const string MySqlQuery = "select ob_version(), @@ob_compatibility_mode, @@autocommit from dual";

        internal static async Task ReplyAsync(PacketChannel channel, bool oracle = true, string version = "4.4.2.2", bool deprecated = false, CancellationToken token = default, string autocommit = "1", bool initializeAutocommit = true)
        {
            channel.BeginCommand();
            var query = await channel.ReadPacketAsync(token);
            Assert.Equal(3, query[0]);
            Assert.Equal(oracle ? OracleQuery : MySqlQuery, Encoding.UTF8.GetString(query, 1, query.Length - 1));
            await channel.WritePacketAsync(new byte[] { (byte)(oracle ? 2 : 3) }, token);
            foreach (var name in new[] { "OB_VERSION()", "@@OB_COMPATIBILITY_MODE" })
            {
                using var column = new MemoryStream();
                foreach (var text in new[] { "def", "", "", "", name, name })
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    column.WriteByte((byte)bytes.Length); column.Write(bytes, 0, bytes.Length);
                }
                column.Write(new byte[] { 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }, 0, 13);
                await channel.WritePacketAsync(column.ToArray(), token);
            }
            if (!oracle)
            {
                using var column = new MemoryStream();
                foreach (var text in new[] { "def", "", "", "", "@@autocommit", "@@autocommit" })
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    column.WriteByte((byte)bytes.Length); column.Write(bytes, 0, bytes.Length);
                }
                column.Write(new byte[] { 12, 63, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 0 }, 0, 13);
                await channel.WritePacketAsync(column.ToArray(), token);
            }
            var eof = new byte[] { 254, 0, 0, 6, 0 };
            if (!deprecated) await channel.WritePacketAsync(eof, token);
            using var row = new MemoryStream();
            foreach (var value in oracle ? new[] { version, "ORACLE" } : new[] { version, "MYSQL", autocommit })
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                row.WriteByte((byte)bytes.Length); row.Write(bytes, 0, bytes.Length);
            }
            await channel.WritePacketAsync(row.ToArray(), token);
            await channel.WritePacketAsync(deprecated ? new byte[] { 254, 0, 0, 6, 0, 0, 0 } : eof, token);
            if (oracle && initializeAutocommit) await ReplyOracleAutocommitAsync(channel, token);
            channel.BeginCommand();
        }

        internal static async Task ReplyOracleAutocommitAsync(PacketChannel channel, CancellationToken token = default)
        {
            channel.BeginCommand();
            var command = await channel.ReadPacketAsync(token);
            Assert.Equal(3, command[0]);
            Assert.Equal("set autocommit = 1", Encoding.ASCII.GetString(command, 1, command.Length - 1));
            await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, token);
            channel.BeginCommand();
        }
    }
}
