using System;
using System.IO;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Values
{
    internal static class IntervalCodec
    {
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
