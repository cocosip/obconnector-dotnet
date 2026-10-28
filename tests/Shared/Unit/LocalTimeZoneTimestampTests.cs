using System;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {

        internal static async Task<byte[]> ReadClientCommand(PacketChannel channel, bool deprecated = false)
        {
            var request = await channel.ReadPacketAsync();
            while (request[0] == 3)
            {
                var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                if (sql == SessionTimeZoneSql) await TimeZoneReply(channel, deprecated: deprecated);
                else if (sql == NationalCharacterSetSql) await NationalCharacterSetReply(channel, deprecated: deprecated);
                else break;
                channel.BeginCommand();
                request = await channel.ReadPacketAsync();
            }
            return request;
        }
        private const string SessionTimeZoneSql = "SELECT SESSIONTIMEZONE FROM DUAL";

        private static async Task TimeZoneReply(PacketChannel channel, string zone = "+00:00", bool deprecated = false)
        {
            await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(Column("SESSIONTIMEZONE", 253));
            if (!deprecated) await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(WithLength(Encoding.UTF8.GetBytes(zone)));
            await channel.WritePacketAsync(deprecated ? TimeZoneOk(null, 254) : Eof());
        }
    }
}
