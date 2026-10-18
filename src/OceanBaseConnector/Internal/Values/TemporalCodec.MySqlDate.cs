using System;
using System.IO;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Values
{
    internal static partial class TemporalCodec
    {
        internal static OceanBaseDateTime DecodeMySqlDate(byte type, byte[] value, bool binary, int scale)
        {
            try
            {
                if (!binary)
                {
                    var text = ResultColumn.Utf8.GetString(value);
                    if (text.Length < 10 || text[4] != '-' || text[7] != '-') throw new FormatException("Invalid MySQL date format.");
                    var date = new OceanBaseDate(Digits(text.Substring(0, 4)), Digits(text.Substring(5, 2)), Digits(text.Substring(8, 2)));
                    if (type == 10)
                    {
                        if (text.Length != 10) throw new FormatException("Invalid MySQL DATE format.");
                        return new OceanBaseDateTime(date, scale: 0);
                    }
                    if (text.Length < 19 || text.Length > 26 || text[10] != ' ' || text[13] != ':' || text[16] != ':' ||
                        text.Length > 19 && (text.Length == 20 || text[19] != '.' || text.Length - 20 > scale))
                        throw new FormatException("Invalid MySQL date/time format or precision.");
                    var micros = text.Length == 19 ? 0 : Digits(text.Substring(20).PadRight(6, '0'));
                    return new OceanBaseDateTime(date, Digits(text.Substring(11, 2)), Digits(text.Substring(14, 2)), Digits(text.Substring(17, 2)), micros, (byte)scale);
                }
                if (value.Length == 0) return new OceanBaseDateTime(default, scale: (byte)scale);
                if (type == 10 ? value.Length != 4 : value.Length != 4 && value.Length != 7 && value.Length != 11)
                    throw new ProtocolException("Invalid MySQL binary date/time length.");
                var cursor = new ByteCursor(value);
                var fields = new OceanBaseDate(cursor.ReadUInt16(), cursor.ReadUInt8(), cursor.ReadUInt8());
                var hour = value.Length >= 7 ? cursor.ReadUInt8() : 0;
                var minute = value.Length >= 7 ? cursor.ReadUInt8() : 0;
                var second = value.Length >= 7 ? cursor.ReadUInt8() : 0;
                var fraction = value.Length == 11 ? cursor.ReadUInt32() : 0;
                if (fraction > 999999) throw new ProtocolException("Invalid MySQL date/time fraction.");
                return new OceanBaseDateTime(fields, hour, minute, second, (int)fraction, (byte)scale);
            }
            catch (ArgumentException error) { throw new FormatException("Invalid MySQL date/time components or precision.", error); }
        }

        internal static void WriteMySqlDate(BinaryWriter writer, OceanBaseDateTime value, bool dateOnly, byte? declaredScale)
        {
            if (declaredScale.HasValue)
                value = new OceanBaseDateTime(value.Date, value.Hour, value.Minute, value.Second, value.Microsecond, declaredScale.Value);
            var date = value.Date;
            if (date.Year == 0 && date.Month == 0 && date.Day == 0 && value.Hour == 0 && value.Minute == 0 && value.Second == 0 && value.Microsecond == 0)
            { writer.Write((byte)0); return; }
            writer.Write((byte)(dateOnly ? 4 : value.Microsecond == 0 ? 7 : 11));
            writer.Write((ushort)date.Year); writer.Write((byte)date.Month); writer.Write((byte)date.Day);
            if (!dateOnly) { writer.Write((byte)value.Hour); writer.Write((byte)value.Minute); writer.Write((byte)value.Second); }
            if (!dateOnly && value.Microsecond != 0) writer.Write((uint)value.Microsecond);
        }
    }
}
