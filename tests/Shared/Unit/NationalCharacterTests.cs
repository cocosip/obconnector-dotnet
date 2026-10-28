using System;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private const string NationalCharacterSetSql = "SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL";

        private static async Task NationalCharacterSetReply(PacketChannel channel, string? national = "utf8mb4", string? results = "utf8mb4", string? connection = "utf8mb4", bool deprecated = false)
        {
            await channel.WritePacketAsync(new byte[] { 3 });
            for (var i = 0; i < 3; i++) await channel.WritePacketAsync(Column("charset", 253));
            if (!deprecated) await channel.WritePacketAsync(Eof());
            var bytes = new System.Collections.Generic.List<byte>();
            foreach (var name in new[] { national, results, connection })
                bytes.AddRange(name == null ? new byte[] { 251 } : WithLength(Encoding.UTF8.GetBytes(name)));
            await channel.WritePacketAsync(bytes.ToArray());
            await channel.WritePacketAsync(deprecated ? TimeZoneOk(null, 254) : Eof());
        }
    }
}
