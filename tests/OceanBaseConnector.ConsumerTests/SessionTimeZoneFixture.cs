using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    internal static class SessionTimeZoneFixture
    {
        internal static readonly byte[] Report = ReportFor("utf8mb4");
        internal static byte[] ReportFor(string national)
        {
            using var state = new MemoryStream();
            var names = new[] { "time_zone", "ncharacter_set_connection", "character_set_results", "character_set_connection" };
            var values = new[] { "+00:00", national, "utf8mb4", "utf8mb4" };
            for (var i = 0; i < names.Length; i++)
            {
                var name = Encoding.UTF8.GetBytes(names[i]); var value = Encoding.UTF8.GetBytes(values[i]);
                state.WriteByte(0); state.WriteByte((byte)(2 + name.Length + value.Length));
                state.WriteByte((byte)name.Length); state.Write(name); state.WriteByte((byte)value.Length); state.Write(value);
            }
            using var packet = new MemoryStream(); packet.Write(new byte[] { 0, 0, 0, 6, 64, 0, 0, 0 });
            packet.WriteByte((byte)state.Length); packet.Write(state.ToArray()); return packet.ToArray();
        }

        internal static async Task<byte[]> ReadCommand(PacketChannel channel, CancellationToken token, bool expectProbe = true, string national = "utf8mb4")
        {
            var request = await channel.ReadPacketAsync(token);
            if (!expectProbe && (request[0] != 3 || Encoding.UTF8.GetString(request, 1, request.Length - 1) != "SELECT SESSIONTIMEZONE FROM DUAL")) return await ReadNationalCommand(channel, token, request, national);
            Assert.Equal(3, request[0]);
            Assert.Equal("SELECT SESSIONTIMEZONE FROM DUAL", Encoding.UTF8.GetString(request, 1, request.Length - 1));
            await channel.WritePacketAsync(new byte[] { 1 }, token);
            await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 122, 1, 122, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }, token);
            await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, token);
            await channel.WritePacketAsync(new byte[] { 6, 43, 48, 48, 58, 48, 48 }, token);
            await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, token);
            channel.BeginCommand();
            return await ReadNationalCommand(channel, token, await channel.ReadPacketAsync(token), national);
        }

        private static async Task<byte[]> ReadNationalCommand(PacketChannel channel, CancellationToken token, byte[] request, string national)
        {
            if (request[0] != 3 || Encoding.UTF8.GetString(request, 1, request.Length - 1) != "SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL") return request;
            await channel.WritePacketAsync(new byte[] { 3 }, token);
            for (var i = 0; i < 3; i++) await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 122, 1, 122, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }, token);
            await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, token);
            using var row = new MemoryStream();
            foreach (var value in new[] { national, "utf8mb4", "utf8mb4" })
            { var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
            await channel.WritePacketAsync(row.ToArray(), token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, token);
            channel.BeginCommand(); return await channel.ReadPacketAsync(token);
        }
    }
}
