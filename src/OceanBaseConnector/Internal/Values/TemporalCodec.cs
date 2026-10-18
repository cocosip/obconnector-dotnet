using System;
using System.Globalization;
using System.IO;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Values
{
    internal static class TemporalCodec
    {
        private const long MaximumTimeTicks = (838 * 3600L + 59 * 60 + 59) * TimeSpan.TicksPerSecond + 9999990;

        internal static byte[] ReadField(ByteCursor cursor, byte type)
        {
            var length = cursor.ReadUInt8();
            var valid = type == 202 ? length == 12 : type == 11 ? length == 0 || length == 8 || length == 12
                : type == 10 ? length == 0 || length == 4 : length == 0 || length == 4 || length == 7 || length == 11;
            if (!valid) throw new ProtocolException("Invalid binary temporal field length.");
            return cursor.ReadBytes(length);
        }

        internal static object DecodeText(byte type, OceanBaseCompatibilityMode mode, string text)
        {
            if (type == 11)
            {
                var negative = text.StartsWith("-", StringComparison.Ordinal);
                if (negative) text = text.Substring(1);
                var parts = text.Split(':');
                if (parts.Length != 3 || parts[0].Length < 2 || parts[0].Length > 3 || parts[1].Length != 2)
                    throw new FormatException("Invalid TIME value.");
                var secondParts = parts[2].Split('.');
                if (secondParts.Length > 2 || secondParts[0].Length != 2 || secondParts.Length == 2 && (secondParts[1].Length < 1 || secondParts[1].Length > 6))
                    throw new FormatException("Invalid TIME value.");
                var hours = Digits(parts[0]); var minutes = Digits(parts[1]); var seconds = Digits(secondParts[0]);
                if (minutes > 59 || seconds > 59) throw new FormatException("Invalid TIME value.");
                var fraction = secondParts.Length == 2 ? Digits(secondParts[1].PadRight(6, '0')) * 10L : 0;
                var ticks = (hours * 3600L + minutes * 60 + seconds) * TimeSpan.TicksPerSecond + fraction;
                ValidateTime(ticks);
                return TimeSpan.FromTicks(negative ? -ticks : ticks);
            }
            var formats = type == 10 ? new[] { "yyyy-MM-dd" } : mode == OceanBaseCompatibilityMode.Oracle
                ? new[] { "yyyy-MM-dd HH:mm:ss" } : new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.FFFFFF" };
            if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                if (text.StartsWith("0000-", StringComparison.Ordinal)) throw new NotSupportedException("Zero dates require the C03 value types.");
                throw new FormatException("Invalid date/time value.");
            }
            return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        }

        internal static object DecodeBinary(byte type, OceanBaseCompatibilityMode mode, byte[] value, int scale)
        {
            var cursor = new ByteCursor(value);
            if (type == 11)
            {
                if (value.Length == 0) return TimeSpan.Zero;
                if (value.Length != 8 && value.Length != 12) throw new ProtocolException("Invalid binary TIME length.");
                var negative = cursor.ReadUInt8();
                var days = cursor.ReadUInt32();
                var hours = cursor.ReadUInt8(); var minutes = cursor.ReadUInt8(); var seconds = cursor.ReadUInt8();
                var microseconds = value.Length == 12 ? cursor.ReadUInt32() : 0;
                if (negative > 1 || days > 34 || hours > 23 || minutes > 59 || seconds > 59 || microseconds > 999999)
                    throw new ProtocolException("Invalid binary TIME components.");
                var ticks = ((days * 24L + hours) * 3600 + minutes * 60 + seconds) * TimeSpan.TicksPerSecond + microseconds * 10L;
                ValidateTime(ticks);
                return TimeSpan.FromTicks(negative == 1 ? -ticks : ticks);
            }
            if (value.Length == 0) throw new NotSupportedException("Zero dates require the C03 value types.");
            if (type == 202)
            {
                if (value.Length != 12) throw new ProtocolException("Invalid Oracle TIMESTAMP length.");
                var century = cursor.ReadUInt8(); var year = cursor.ReadUInt8();
                if (century > 99 || year > 99) throw new FormatException("The timestamp year is outside the DateTime range.");
                var date = Date(century * 100 + year, cursor.ReadUInt8(), cursor.ReadUInt8(), cursor.ReadUInt8(), cursor.ReadUInt8(), cursor.ReadUInt8());
                var nanoseconds = cursor.ReadUInt32();
                var valueScale = cursor.ReadUInt8();
                if (nanoseconds > 999999999 || valueScale > 9) throw new ProtocolException("Invalid Oracle TIMESTAMP fraction.");
                if (valueScale > 7 || valueScale != scale || nanoseconds % (uint)Math.Pow(10, 9 - scale) != 0)
                    throw new NotSupportedException("The timestamp fraction cannot be represented at its declared precision.");
                return date.AddTicks(nanoseconds / 100);
            }
            if (value.Length != 4 && value.Length != 7 && value.Length != 11) throw new ProtocolException("Invalid binary date/time length.");
            var y = cursor.ReadUInt16(); var month = cursor.ReadUInt8(); var day = cursor.ReadUInt8();
            if (y == 0 || month == 0 || day == 0) throw new NotSupportedException("Zero dates require the C03 value types.");
            var hour = value.Length >= 7 ? cursor.ReadUInt8() : 0;
            var minute = value.Length >= 7 ? cursor.ReadUInt8() : 0;
            var second = value.Length >= 7 ? cursor.ReadUInt8() : 0;
            var micros = value.Length == 11 ? cursor.ReadUInt32() : 0;
            if (micros > 999999) throw new ProtocolException("Invalid date/time fraction.");
            if (mode == OceanBaseCompatibilityMode.Oracle && micros != 0)
                throw new NotSupportedException("Oracle DATE cannot contain fractional seconds.");
            return Date(y, month, day, hour, minute, second).AddTicks(micros * 10L);
        }

        internal static void WriteDate(BinaryWriter writer, ushort type, DateTime date, byte? declaredScale)
        {
            if (date.Kind != DateTimeKind.Unspecified) throw new ArgumentException("Unzoned date/time parameters require DateTimeKind.Unspecified.");
            var fraction = date.Ticks % TimeSpan.TicksPerSecond;
            var maximumScale = type == 202 ? 7 : type == 12 ? 6 : 0;
            var scale = declaredScale ?? maximumScale;
            if (scale > maximumScale || fraction % (long)Math.Pow(10, 7 - scale) != 0)
                throw new ArgumentException("The date/time precision would require truncation.");
            if (type == 10 && date.TimeOfDay != TimeSpan.Zero) throw new ArgumentException("A MySQL DATE cannot retain the time component.");
            if (type == 202)
            {
                writer.Write((byte)12); writer.Write((byte)(date.Year / 100)); writer.Write((byte)(date.Year % 100));
                writer.Write((byte)date.Month); writer.Write((byte)date.Day);
                writer.Write((byte)date.Hour); writer.Write((byte)date.Minute); writer.Write((byte)date.Second);
                writer.Write((uint)(fraction * 100)); writer.Write((byte)scale);
            }
            else
            {
                writer.Write((byte)(type == 10 ? 4 : fraction == 0 ? 7 : 11));
                writer.Write((ushort)date.Year); writer.Write((byte)date.Month); writer.Write((byte)date.Day);
                if (type != 10) { writer.Write((byte)date.Hour); writer.Write((byte)date.Minute); writer.Write((byte)date.Second); }
                if (fraction != 0) writer.Write((uint)(fraction / 10));
            }
        }

        internal static void WriteTime(BinaryWriter writer, TimeSpan value, byte? declaredScale)
        {
            var ticks = value.Ticks;
            if (ticks < -MaximumTimeTicks || ticks > MaximumTimeTicks) throw new ArgumentOutOfRangeException(nameof(value), "TIME exceeds 838:59:59.999999.");
            var scale = declaredScale ?? 6;
            if (scale > 6 || ticks % (long)Math.Pow(10, 7 - scale) != 0) throw new ArgumentException("TIME precision would require truncation.");
            var magnitude = ticks < 0 ? -ticks : ticks;
            if (magnitude == 0) { writer.Write((byte)0); return; }
            var fraction = magnitude % TimeSpan.TicksPerSecond;
            writer.Write((byte)(fraction == 0 ? 8 : 12)); writer.Write((byte)(ticks < 0 ? 1 : 0));
            var absolute = TimeSpan.FromTicks(magnitude);
            writer.Write((uint)absolute.Days); writer.Write((byte)absolute.Hours); writer.Write((byte)absolute.Minutes); writer.Write((byte)absolute.Seconds);
            if (fraction != 0) writer.Write((uint)(fraction / 10));
        }

        private static DateTime Date(int year, int month, int day, int hour, int minute, int second)
        {
            try { return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified); }
            catch (ArgumentOutOfRangeException) { throw new FormatException("Invalid date/time components."); }
        }
        private static int Digits(string value)
        {
            var number = 0;
            foreach (var digit in value)
            {
                if (digit < '0' || digit > '9') throw new FormatException("Invalid TIME value.");
                number = checked(number * 10 + digit - '0');
            }
            return number;
        }
        private static void ValidateTime(long ticks)
        { if (ticks > MaximumTimeTicks) throw new OverflowException("TIME exceeds 838:59:59.999999."); }
    }
}
