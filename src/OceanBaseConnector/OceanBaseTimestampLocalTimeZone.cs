using System;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector
{
    /// <summary>Exact local timestamp fields and the server session time zone used to represent them.</summary>
    public readonly struct OceanBaseTimestampLocalTimeZone
    {
        private readonly string? _sessionTimeZone;

        public OceanBaseTimestampLocalTimeZone(OceanBaseTimestamp timestamp, string sessionTimeZone)
        {
            ValidateTimeZone(sessionTimeZone);
            Timestamp = timestamp; _sessionTimeZone = sessionTimeZone;
        }

        public OceanBaseTimestamp Timestamp { get; }
        public string SessionTimeZone => _sessionTimeZone ?? throw new InvalidOperationException("The timestamp has no session time zone.");
        /// <summary>Converts the local fields exactly, without resolving an offset or UTC instant.</summary>
        public DateTime ToDateTime() => Timestamp.ToDateTime();
        public override string ToString() => Timestamp + " [" + SessionTimeZone + "]";

        internal static void ValidateTimeZone(string? zone)
        {
            if (string.IsNullOrEmpty(zone) || zone!.Length > 255 || ResultColumn.Utf8.GetByteCount(zone) > 255 ||
                string.Equals(zone, "SYSTEM", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A resolved server session time zone is required.");
            foreach (var c in zone) if (char.IsWhiteSpace(c) || char.IsControl(c)) throw new ArgumentException("Invalid session time zone identity.");
            if (zone[0] != '+' && zone[0] != '-') return;
            if (zone.Length != 6 || zone[3] != ':' || zone[1] < '0' || zone[1] > '9' || zone[2] < '0' || zone[2] > '9' ||
                zone[4] < '0' || zone[4] > '9' || zone[5] < '0' || zone[5] > '9') throw new ArgumentException("Invalid session time zone offset.");
            var hours = (zone[1] - '0') * 10 + zone[2] - '0';
            var minutes = (zone[4] - '0') * 10 + zone[5] - '0';
            if (hours > 14 || minutes > 59 || hours == 14 && minutes != 0) throw new ArgumentException("Session time zone offset is out of range.");
        }
    }
}
