using System;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {

        internal static byte[] SessionVariablesOk(System.Collections.Generic.Dictionary<string, string> values, byte header = 0, byte status = 6)
        {
            using var state = new MemoryStream();
            foreach (var pair in values)
            {
                var name = Encoding.UTF8.GetBytes(pair.Key); var value = Encoding.UTF8.GetBytes(pair.Value);
                state.WriteByte(0); state.WriteByte((byte)(2 + name.Length + value.Length));
                state.WriteByte((byte)name.Length); state.Write(name);
                state.WriteByte((byte)value.Length); state.Write(value);
            }
            using var stream = new MemoryStream();
            stream.Write(new byte[] { header, 0, 0, status, 64, 0, 0, 0 });
            stream.Write(WithLength(state.ToArray()));
            return stream.ToArray();
        }
        internal static byte[] TimeZoneOk(string? zone, byte header = 0, byte status = 6)
        {
            if (zone == null) { var plain = Ok(status: status); plain[0] = header; return plain; }
            return SessionVariablesOk(new NationalCharacterSet("utf8mb4", "utf8mb4", "utf8mb4").Variables(zone), header, status);
        }
    }
}
