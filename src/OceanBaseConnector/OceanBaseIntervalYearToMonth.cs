using System;
using System.Globalization;

namespace OceanBaseConnector
{
    /// <summary>An exact Oracle year-to-month interval; months are not converted to a fixed duration.</summary>
    public readonly struct OceanBaseIntervalYearToMonth
    {
        public OceanBaseIntervalYearToMonth(bool isNegative, int years, int months, byte leadingPrecision = 9)
        {
            if (leadingPrecision > 9) throw new ArgumentOutOfRangeException(nameof(leadingPrecision));
            if (years < 0 || years >= (long)Math.Pow(10, leadingPrecision)) throw new ArgumentOutOfRangeException(nameof(years));
            if (months < 0 || months > 11) throw new ArgumentOutOfRangeException(nameof(months));
            IsNegative = isNegative; Years = years; Months = months; LeadingPrecision = leadingPrecision;
        }

        public bool IsNegative { get; }
        public int Years { get; }
        public int Months { get; }
        public byte LeadingPrecision { get; }

        public override string ToString() => (IsNegative ? "-" : "+") +
            Years.ToString("D" + Math.Max(1, (int)LeadingPrecision), CultureInfo.InvariantCulture) + "-" +
            Months.ToString("D2", CultureInfo.InvariantCulture);
    }
}
