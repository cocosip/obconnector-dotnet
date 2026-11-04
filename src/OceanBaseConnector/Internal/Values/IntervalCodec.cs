using System;
using System.IO;
using System.Text.RegularExpressions;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Values
{
    internal static class IntervalCodec
    {
        private static readonly Regex YearMonthDeclaration = new Regex(@"\AINTERVAL YEAR(?:\(([0-9])\))? TO MONTH\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex DaySecondDeclaration = new Regex(@"\AINTERVAL DAY(?:\(([0-9])\))? TO SECOND(?:\(([0-9])\))?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        internal static (byte Leading, byte Fraction) GetTextPrecisions(string declaration, bool daySecond)
        {
            var match = (daySecond ? DaySecondDeclaration : YearMonthDeclaration).Match(declaration);
            if (!match.Success) throw new NotSupportedException("The complex text INTERVAL declaration is not supported.");
            return (match.Groups[1].Success ? (byte)(match.Groups[1].Value[0] - '0') : (byte)2,
                daySecond ? match.Groups[2].Success ? (byte)(match.Groups[2].Value[0] - '0') : (byte)6 : (byte)0);
        }

        internal static object DecodeText(string value, bool daySecond, string declaration)
        {
            var precision = GetTextPrecisions(declaration, daySecond);
            var width = Math.Max(1, (int)precision.Leading);
            var separator = 1 + width;
            var length = separator + (daySecond ? 9 + (precision.Fraction == 0 ? 0 : 1 + precision.Fraction) : 3);
            if (value.Length != length || value[0] != '+' && value[0] != '-' ||
                value[separator] != (daySecond ? ' ' : '-') || daySecond &&
                (value[separator + 3] != ':' || value[separator + 6] != ':' || precision.Fraction != 0 && value[separator + 9] != '.'))
                throw new ProtocolException("Invalid complex text INTERVAL format.");
            try
            {
                var leading = ReadTextComponent(value, 1, width);
                if (!daySecond) return new OceanBaseIntervalYearToMonth(value[0] == '-', leading,
                    ReadTextComponent(value, separator + 1, 2), precision.Leading);
                var nanos = precision.Fraction == 0 ? 0 : ReadTextComponent(value, separator + 10, precision.Fraction) * (int)Math.Pow(10, 9 - precision.Fraction);
                return new OceanBaseIntervalDayToSecond(value[0] == '-', leading, ReadTextComponent(value, separator + 1, 2),
                    ReadTextComponent(value, separator + 4, 2), ReadTextComponent(value, separator + 7, 2), nanos, precision.Leading, precision.Fraction);
            }
            catch (ArgumentException) { throw new ProtocolException("Invalid complex text INTERVAL components or precision."); }
        }

        private static int ReadTextComponent(string value, int offset, int length)
        {
            var result = 0;
            for (var i = offset; i < offset + length; i++)
            {
                var digit = value[i] - '0';
                if (digit < 0 || digit > 9) throw new ProtocolException("Invalid complex text INTERVAL component.");
                result = result * 10 + digit;
            }
            return result;
        }

        internal static object Decode(byte[] bytes, bool daySecond, int scale)
        {
            if (bytes.Length != (daySecond ? 14 : 7)) throw new ProtocolException("Invalid Oracle INTERVAL length.");
            var cursor = new ByteCursor(bytes);
            var sign = cursor.ReadUInt8();
            if (sign > 1) throw new ProtocolException("Invalid Oracle INTERVAL sign.");
            var leading = cursor.ReadUInt32();
            try
            {
                if (!daySecond)
                {
                    var months = cursor.ReadUInt8(); var precision = cursor.ReadUInt8();
                    if (precision != scale) throw new ProtocolException("INTERVAL precision does not match its column.");
                    return new OceanBaseIntervalYearToMonth(sign == 1, checked((int)leading), months, precision);
                }
                var hours = cursor.ReadUInt8(); var minutes = cursor.ReadUInt8(); var seconds = cursor.ReadUInt8();
                var nanos = cursor.ReadUInt32(); var dayPrecision = cursor.ReadUInt8(); var fractionScale = cursor.ReadUInt8();
                if (dayPrecision * 10 + fractionScale != scale) throw new ProtocolException("INTERVAL precision does not match its column.");
                return new OceanBaseIntervalDayToSecond(sign == 1, checked((int)leading), hours, minutes, seconds,
                    checked((int)nanos), dayPrecision, fractionScale);
            }
            catch (ArgumentException) { throw new ProtocolException("Invalid Oracle INTERVAL components or precision."); }
            catch (OverflowException) { throw new ProtocolException("Invalid Oracle INTERVAL component range."); }
        }

        internal static void Write(BinaryWriter writer, object value, bool daySecond, byte precision, byte? scale)
        {
            if (!daySecond && value is OceanBaseIntervalYearToMonth ym)
            {
                ym = new OceanBaseIntervalYearToMonth(ym.IsNegative, ym.Years, ym.Months, precision == 0 ? ym.LeadingPrecision : precision);
                writer.Write((byte)7); writer.Write((byte)(ym.IsNegative ? 1 : 0)); writer.Write(ym.Years);
                writer.Write((byte)ym.Months); writer.Write(ym.LeadingPrecision);
            }
            else if (daySecond && value is OceanBaseIntervalDayToSecond ds)
            {
                ds = new OceanBaseIntervalDayToSecond(ds.IsNegative, ds.Days, ds.Hours, ds.Minutes, ds.Seconds,
                    ds.Nanosecond, precision == 0 ? ds.LeadingPrecision : precision, scale ?? ds.Scale);
                writer.Write((byte)14); writer.Write((byte)(ds.IsNegative ? 1 : 0)); writer.Write(ds.Days);
                writer.Write((byte)ds.Hours); writer.Write((byte)ds.Minutes); writer.Write((byte)ds.Seconds);
                writer.Write(ds.Nanosecond); writer.Write(ds.LeadingPrecision); writer.Write(ds.Scale);
            }
            else throw new ArgumentException("An INTERVAL parameter requires the corresponding exact provider value.");
        }
    }
}
