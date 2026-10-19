using System;
using System.Globalization;

namespace OceanBaseConnector
{
    /// <summary>An exact Oracle day-to-second interval with a nanosecond fraction and declared precisions.</summary>
    public readonly struct OceanBaseIntervalDayToSecond
    {
        public OceanBaseIntervalDayToSecond(bool isNegative, int days, int hours, int minutes, int seconds,
            int nanosecond = 0, byte leadingPrecision = 9, byte scale = 9)
        {
            if (leadingPrecision > 9) throw new ArgumentOutOfRangeException(nameof(leadingPrecision));
            if (days < 0 || days >= (long)Math.Pow(10, leadingPrecision)) throw new ArgumentOutOfRangeException(nameof(days));
            if (hours < 0 || hours > 23) throw new ArgumentOutOfRangeException(nameof(hours));
            if (minutes < 0 || minutes > 59) throw new ArgumentOutOfRangeException(nameof(minutes));
            if (seconds < 0 || seconds > 59) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (scale > 9) throw new ArgumentOutOfRangeException(nameof(scale));
            if (nanosecond < 0 || nanosecond > 999999999) throw new ArgumentOutOfRangeException(nameof(nanosecond));
            if (nanosecond % (int)Math.Pow(10, 9 - scale) != 0)
                throw new ArgumentException("The interval scale would require rounding.", nameof(nanosecond));
            IsNegative = isNegative; Days = days; Hours = hours; Minutes = minutes; Seconds = seconds;
            Nanosecond = nanosecond; LeadingPrecision = leadingPrecision; Scale = scale;
        }

        public bool IsNegative { get; }
        public int Days { get; }
        public int Hours { get; }
        public int Minutes { get; }
        public int Seconds { get; }
        public int Nanosecond { get; }
        public byte LeadingPrecision { get; }
        public byte Scale { get; }

        /// <summary>Converts exactly, throwing OverflowException for excess range or sub-tick precision.</summary>
        public TimeSpan ToTimeSpan()
        {
            if (Nanosecond % 100 != 0) throw new OverflowException("The interval cannot be represented exactly as TimeSpan.");
            var sign = IsNegative ? -1L : 1L;
            var seconds = ((long)Days * 86400 + Hours * 3600 + Minutes * 60 + Seconds) * sign;
            return new TimeSpan(checked(seconds * TimeSpan.TicksPerSecond + Nanosecond / 100 * sign));
        }

        public override string ToString() => (IsNegative ? "-" : "+") +
            Days.ToString("D" + Math.Max(1, (int)LeadingPrecision), CultureInfo.InvariantCulture) + " " +
            Hours.ToString("D2", CultureInfo.InvariantCulture) + ":" + Minutes.ToString("D2", CultureInfo.InvariantCulture) + ":" +
            Seconds.ToString("D2", CultureInfo.InvariantCulture) +
            (Scale == 0 ? "" : "." + Nanosecond.ToString("D9", CultureInfo.InvariantCulture).Substring(0, Scale));
    }
}
