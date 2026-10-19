using System;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Values
{
    internal static class BitCodec
    {
        internal static bool Fits(ulong value, int width) => width == 64 || value >> width == 0;

        internal static ulong Decode(byte[] value, int width)
        {
            if (value.Length != (width + 7) / 8) throw new ProtocolException("BIT payload length does not match its declared width.");
            ulong bits = 0;
            foreach (var part in value) bits = (bits << 8) | part;
            if (!Fits(bits, width)) throw new FormatException("BIT payload exceeds its declared width.");
            return bits;
        }
    }
}
