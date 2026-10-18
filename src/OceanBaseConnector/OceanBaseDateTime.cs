using System;
using System.Globalization;

namespace OceanBaseConnector
{
    /// <summary>Unzoned MySQL date/time components with exact microseconds and optional zero date components.</summary>
    public readonly struct OceanBaseDateTime
    {
        public OceanBaseDateTime(OceanBaseDate date, int hour = 0, int minute = 0, int second = 0,
            int microsecond = 0, byte scale = 6)
        {
            if (hour < 0 || hour > 23) throw new ArgumentOutOfRangeException(nameof(hour));
            if (minute < 0 || minute > 59) throw new ArgumentOutOfRangeException(nameof(minute));
            if (second < 0 || second > 59) throw new ArgumentOutOfRangeException(nameof(second));
            if (microsecond < 0 || microsecond > 999999) throw new ArgumentOutOfRangeException(nameof(microsecond));
            if (scale > 6) throw new ArgumentOutOfRangeException(nameof(scale));
            if (microsecond % (int)Math.Pow(10, 6 - scale) != 0) throw new ArgumentException("The date/time scale would require rounding.", nameof(microsecond));
            Date = date; Hour = hour; Minute = minute; Second = second; Microsecond = microsecond; Scale = scale;
        }

        public OceanBaseDateTime(DateTime value)
        {
            if (value.Ticks % 10 != 0) throw new ArgumentException("MySQL date/time cannot retain sub-microsecond ticks.", nameof(value));
            this = new OceanBaseDateTime(new OceanBaseDate(value.Date), value.Hour, value.Minute, value.Second,
                (int)(value.Ticks % TimeSpan.TicksPerSecond / 10));
        }

        public OceanBaseDate Date { get; }
        public int Hour { get; }
        public int Minute { get; }
        public int Second { get; }
        public int Microsecond { get; }
        public byte Scale { get; }
        public bool IsValidDateTime => Date.IsValidDate;
        public DateTime ToDateTime() => Date.ToDateTime().AddHours(Hour).AddMinutes(Minute).AddSeconds(Second).AddTicks(Microsecond * 10L);
        public override string ToString() => Date + " " + Hour.ToString("D2", CultureInfo.InvariantCulture) + ":" +
            Minute.ToString("D2", CultureInfo.InvariantCulture) + ":" + Second.ToString("D2", CultureInfo.InvariantCulture) +
            (Scale == 0 ? "" : "." + Microsecond.ToString("D6", CultureInfo.InvariantCulture).Substring(0, Scale));
    }
}
