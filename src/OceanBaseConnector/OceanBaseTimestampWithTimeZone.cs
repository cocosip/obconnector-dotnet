using System;
using System.Globalization;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector
{
    /// <summary>An exact local timestamp, UTC offset, and optional server time zone identity.</summary>
    public readonly struct OceanBaseTimestampWithTimeZone
    {
        public OceanBaseTimestampWithTimeZone(OceanBaseTimestamp timestamp, TimeSpan offset,
            string? timeZoneName = null, string? timeZoneAbbreviation = null)
        {
            if (offset.Ticks % TimeSpan.TicksPerMinute != 0 || offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14))
                throw new ArgumentOutOfRangeException(nameof(offset), "The UTC offset must be whole minutes within -14:00..+14:00.");
            Timestamp = timestamp; Offset = offset;
            TimeZoneName = ValidateName(timeZoneName); TimeZoneAbbreviation = ValidateName(timeZoneAbbreviation);
        }

        public OceanBaseTimestampWithTimeZone(DateTimeOffset value)
            : this(new OceanBaseTimestamp(value.DateTime), value.Offset)
        { }

        public OceanBaseTimestamp Timestamp { get; }
        public TimeSpan Offset { get; }
        public string? TimeZoneName { get; }
        public string? TimeZoneAbbreviation { get; }

        /// <summary>Converts exactly, rejecting sub-tick precision or an out-of-range UTC instant.</summary>
        public DateTimeOffset ToDateTimeOffset() => new DateTimeOffset(Timestamp.ToDateTime(), Offset);

        public override string ToString() => Timestamp + " " + (Offset < TimeSpan.Zero ? "-" : "+") +
            Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture) +
            (TimeZoneName == null ? "" : " [" + TimeZoneName + "]") +
            (TimeZoneAbbreviation == null ? "" : " [" + TimeZoneAbbreviation + "]");

        private static string? ValidateName(string? name)
        {
            if (name == null || name.Length == 0) return null;
            if (ResultColumn.Utf8.GetByteCount(name) > byte.MaxValue) throw new ArgumentException("The time zone identity exceeds the protocol length.");
            foreach (var c in name) if (char.IsControl(c)) throw new ArgumentException("A time zone identity cannot contain control characters.");
            return name;
        }
    }
}
