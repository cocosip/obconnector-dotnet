using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Numerics;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {

        private static byte[] WithLength(byte[] value)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            var length = value.Length;
            if (length < 251) writer.Write((byte)length);
            else if (length <= ushort.MaxValue) { writer.Write((byte)252); writer.Write((ushort)length); }
            else if (length <= 0xFFFFFF) { writer.Write((byte)253); writer.Write((byte)length); writer.Write((byte)(length >> 8)); writer.Write((byte)(length >> 16)); }
            else { writer.Write((byte)254); writer.Write((ulong)length); }
            writer.Write(value);
            return stream.ToArray();
        }
    }
}
