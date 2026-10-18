using System;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Server ERR packet: 0xFF header, error code (2, LE), optional
    /// '#'-prefixed 5-character SQLSTATE, then the message. The message is
    /// server-provided and may surface in exceptions, so no client-side
    /// secrets (SQL text, bind values, credentials) may ever be merged into it.
    /// </summary>
    internal sealed class ErrPacket
    {
        private ErrPacket(int errorCode, string? sqlState, string message)
        {
            ErrorCode = errorCode;
            SqlState = sqlState;
            Message = message;
        }

        public int ErrorCode { get; }

        public string? SqlState { get; }

        public string Message { get; }

        public static bool IsErr(byte[] payload) => payload.Length > 0 && payload[0] == 0xFF;

        public static ErrPacket Parse(byte[] payload)
        {
            if (payload.Length < 3)
            {
                throw new ProtocolException("ERR packet is truncated before the error code.");
            }

            var errorCode = payload[1] | (payload[2] << 8);

            string? sqlState = null;
            var messageOffset = 3;
            if (payload.Length >= 9 && payload[3] == (byte)'#')
            {
                sqlState = Encoding.ASCII.GetString(payload, 4, 5);
                messageOffset = 9;
            }

            var message = Encoding.UTF8.GetString(payload, messageOffset, payload.Length - messageOffset);
            return new ErrPacket(errorCode, sqlState, message);
        }
    }
}
