using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    internal static class SessionTimeZoneFixture
    {
        internal static readonly byte[] Report = new byte[] { 0, 0, 0, 6, 64, 0, 0, 0, 19, 0, 17, 9,
            116, 105, 109, 101, 95, 122, 111, 110, 101, 6, 43, 48, 48, 58, 48, 48 };

        internal static async Task<byte[]> ReadCommand(PacketChannel channel, CancellationToken token, bool expectProbe = true)
        {
            var request = await channel.ReadPacketAsync(token);
            if (!expectProbe && (request[0] != 3 || Encoding.UTF8.GetString(request, 1, request.Length - 1) != "SELECT SESSIONTIMEZONE FROM DUAL")) return request;
            Assert.Equal(3, request[0]);
            Assert.Equal("SELECT SESSIONTIMEZONE FROM DUAL", Encoding.UTF8.GetString(request, 1, request.Length - 1));
            await channel.WritePacketAsync(new byte[] { 1 }, token);
            await channel.WritePacketAsync(new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 122, 1, 122, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }, token);
            await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, token);
            await channel.WritePacketAsync(new byte[] { 6, 43, 48, 48, 58, 48, 48 }, token);
            await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, token);
            channel.BeginCommand();
            return await channel.ReadPacketAsync(token);
        }
    }
}
