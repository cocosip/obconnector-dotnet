using System;
using System.Globalization;

namespace OceanBaseConnector
{
    /// <summary>MySQL date components, including zero components that DateTime cannot represent.</summary>
    public readonly struct OceanBaseDate
    {
        public OceanBaseDate(int year, int month, int day)
        {
            if (year < 0 || year > 9999) throw new ArgumentOutOfRangeException(nameof(year));
            if (month < 0 || month > 12) throw new ArgumentOutOfRangeException(nameof(month));
            if (day < 0 || day > 31 || month != 0 && day > DateTime.DaysInMonth(year == 0 ? 400 : year, month))
                throw new ArgumentOutOfRangeException(nameof(day));
            Year = year; Month = month; Day = day;
        }

        public OceanBaseDate(DateTime value) : this(value.Year, value.Month, value.Day)
        {
            if (value.Kind != DateTimeKind.Unspecified || value.TimeOfDay != TimeSpan.Zero)
                throw new ArgumentException("A date requires Unspecified midnight fields.", nameof(value));
        }

        public int Year { get; }
        public int Month { get; }
        public int Day { get; }
        public bool IsValidDate => Year != 0 && Month != 0 && Day != 0;
        public DateTime ToDateTime() => IsValidDate ? new DateTime(Year, Month, Day)
            : throw new InvalidCastException("Zero date components cannot be represented as DateTime.");
        public override string ToString() => Year.ToString("D4", CultureInfo.InvariantCulture) + "-" +
            Month.ToString("D2", CultureInfo.InvariantCulture) + "-" + Day.ToString("D2", CultureInfo.InvariantCulture);
    }
}
