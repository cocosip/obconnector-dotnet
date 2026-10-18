using System;
using System.Collections.Generic;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Server OK packet: 0x00 header, affected rows (lenenc), last insert id
    /// (lenenc), status flags (2), warnings (2, both with PROTOCOL_41), then
    /// the human-readable info string. With CLIENT_SESSION_TRACK negotiated
    /// and SERVER_SESSION_STATE_CHANGED set, the info area carries a
    /// length-encoded session-state blob of typed entries; system-variable
    /// entries are collected into <see cref="SessionVariables"/> (that is
    /// where <c>ob_capability_flag</c> arrives).
    /// </summary>
    internal sealed class OkPacket
    {
        public const byte SessionTrackSystemVariables = 0;

        private OkPacket(
            long affectedRows,
            long lastInsertId,
            ServerStatusFlags statusFlags,
            ushort warnings,
            string info,
            IReadOnlyDictionary<string, string> sessionVariables)
        {
            AffectedRows = affectedRows;
            LastInsertId = lastInsertId;
            StatusFlags = statusFlags;
            Warnings = warnings;
            Info = info;
            SessionVariables = sessionVariables;
        }

        public long AffectedRows { get; }

        public long LastInsertId { get; }

        public ServerStatusFlags StatusFlags { get; }

        public ushort Warnings { get; }

        public string Info { get; }

        public IReadOnlyDictionary<string, string> SessionVariables { get; }

        public static bool IsOk(byte[] payload) => payload.Length > 0 && payload[0] == 0x00;

        public static OkPacket Parse(byte[] payload, CapabilityFlags effectiveCapabilities, bool allowEofHeader = false)
        {
            var cursor = new ByteCursor(payload);
            var header = cursor.ReadUInt8();
            if (header != 0x00 && !(allowEofHeader && header == 0xFE))
            {
                throw new ProtocolException("Not an OK packet.");
            }

            var affectedRows = cursor.ReadLengthEncodedInteger();
            var lastInsertId = cursor.ReadLengthEncodedInteger();

            ServerStatusFlags statusFlags;
            ushort warnings;
            if ((effectiveCapabilities & CapabilityFlags.ClientProtocol41) != 0)
            {
                statusFlags = (ServerStatusFlags)cursor.ReadUInt16();
                warnings = cursor.ReadUInt16();
            }
            else if ((effectiveCapabilities & CapabilityFlags.ClientTransactions) != 0)
            {
                statusFlags = (ServerStatusFlags)cursor.ReadUInt16();
                warnings = 0;
            }
            else
            {
                statusFlags = ServerStatusFlags.None;
                warnings = 0;
            }

            var info = string.Empty;
            var sessionVariables = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();

            if ((effectiveCapabilities & CapabilityFlags.ClientSessionTrack) != 0)
            {
                if (cursor.HasRemaining) info = Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes());
                if ((statusFlags & ServerStatusFlags.ServerSessionStateChanged) != 0)
                {
                    var stateBytes = cursor.ReadLengthEncodedBytes();
                    sessionVariables = ParseSessionState(stateBytes);
                }
                if (cursor.HasRemaining) throw new ProtocolException("Trailing bytes in OK packet.");
            }
            else if (cursor.HasRemaining)
            {
                info = Encoding.UTF8.GetString(cursor.ReadRemaining());
            }

            return new OkPacket(affectedRows, lastInsertId, statusFlags, warnings, info, sessionVariables);
        }

        /// <summary>
        /// Session-state blob: repeated entries of { type (1), then per type a
        /// payload }. Only SESSION_TRACK_SYSTEM_VARIABLES (0) entries are
        /// decoded: a length-encoded name/value pair.
        /// </summary>
        private static IReadOnlyDictionary<string, string> ParseSessionState(byte[] stateBytes)
        {
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            var cursor = new ByteCursor(stateBytes);
            while (cursor.HasRemaining)
            {
                var entryType = cursor.ReadUInt8();
                var entry = new ByteCursor(cursor.ReadLengthEncodedBytes());
                if (entryType == SessionTrackSystemVariables)
                {
                    var name = Encoding.UTF8.GetString(entry.ReadLengthEncodedBytes());
                    var value = Encoding.UTF8.GetString(entry.ReadLengthEncodedBytes());
                    variables[name] = value;
                    if (entry.HasRemaining) throw new ProtocolException("Trailing system-variable state bytes.");
                }
            }

            return variables;
        }
    }
}
