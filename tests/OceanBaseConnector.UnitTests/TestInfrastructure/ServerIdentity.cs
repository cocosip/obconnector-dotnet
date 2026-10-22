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
        internal static async Task ReplyAsync(PacketChannel channel, bool oracle = true, string version = "4.4.2.2", bool deprecated = false, CancellationToken token = default)
        {
            channel.BeginCommand();
            var query = await channel.ReadPacketAsync(token);
            Assert.Equal(3, query[0]);
            Assert.Equal("select ob_version(), @@ob_compatibility_mode from dual", Encoding.UTF8.GetString(query, 1, query.Length - 1));
            await channel.WritePacketAsync(new byte[] { 2 }, token);
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
            var eof = new byte[] { 254, 0, 0, 6, 0 };
            if (!deprecated) await channel.WritePacketAsync(eof, token);
            using var row = new MemoryStream();
            foreach (var value in new[] { version, oracle ? "ORACLE" : "MYSQL" })
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                row.WriteByte((byte)bytes.Length); row.Write(bytes, 0, bytes.Length);
            }
            await channel.WritePacketAsync(row.ToArray(), token);
            await channel.WritePacketAsync(deprecated ? new byte[] { 254, 0, 0, 6, 0, 0, 0 } : eof, token);
            channel.BeginCommand();
        }
    }
}
