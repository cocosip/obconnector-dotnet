using System;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Values
{
    internal static class YearCodec
    {
        internal static bool IsValid(ushort year) => year == 0 || year >= 1901 && year <= 2155;

        internal static ushort Decode(byte[] value, bool binary)
        {
            ushort year;
            if (binary)
            {
                if (value.Length != 2) throw new ProtocolException("A YEAR field requires two bytes.");
                year = (ushort)(value[0] | value[1] << 8);
            }
            else
            {
                if (value.Length != 4) throw new FormatException("A YEAR field requires four ASCII digits.");
                var number = 0;
                foreach (var digit in value)
                {
                    if (digit < '0' || digit > '9') throw new FormatException("A YEAR field requires four ASCII digits.");
                    number = number * 10 + digit - '0';
                }
                year = (ushort)number;
            }
            if (!IsValid(year)) throw new FormatException("YEAR must be zero or between 1901 and 2155.");
            return year;
        }
    }
}
