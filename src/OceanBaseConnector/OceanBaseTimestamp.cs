using System;
using System.Globalization;

namespace OceanBaseConnector
{
    /// <summary>An unzoned timestamp with an exact nanosecond fraction and declared scale.</summary>
    public readonly struct OceanBaseTimestamp
    {
        public OceanBaseTimestamp(DateTime wholeSeconds, int nanosecond, byte scale = 9)
        {
            if (wholeSeconds.Kind != DateTimeKind.Unspecified || wholeSeconds.Ticks % TimeSpan.TicksPerSecond != 0)
                throw new ArgumentException("An unzoned timestamp requires Unspecified whole seconds.", nameof(wholeSeconds));
            if (scale > 9) throw new ArgumentOutOfRangeException(nameof(scale));
            if (nanosecond < 0 || nanosecond > 999999999) throw new ArgumentOutOfRangeException(nameof(nanosecond));
            if (nanosecond % (int)Math.Pow(10, 9 - scale) != 0)
                throw new ArgumentException("The timestamp scale would require rounding.", nameof(nanosecond));
            WholeSeconds = wholeSeconds; Nanosecond = nanosecond; Scale = scale;
        }

        public OceanBaseTimestamp(DateTime value)
            : this(new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind),
            (int)(value.Ticks % TimeSpan.TicksPerSecond * 100), 7)
        { }

        public DateTime WholeSeconds { get; }
        public int Nanosecond { get; }
        public byte Scale { get; }

        /// <summary>Converts exactly, throwing OverflowException instead of discarding nanoseconds.</summary>
        public DateTime ToDateTime()
        {
            if (Nanosecond % 100 != 0) throw new OverflowException("The timestamp cannot be represented exactly as DateTime.");
            return WholeSeconds.AddTicks(Nanosecond / 100);
        }

        public override string ToString() => WholeSeconds.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
            (Scale == 0 ? "" : "." + Nanosecond.ToString("D9", CultureInfo.InvariantCulture).Substring(0, Scale));
    }
}
