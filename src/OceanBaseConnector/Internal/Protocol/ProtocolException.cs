using System;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Framing and wire-format violation (truncated packet, sequence
    /// mismatch, size-limit breach). Session-level error mapping to
    /// <c>OceanBaseException</c> is delivered by A06; messages here never
    /// contain payload bytes or credentials.
    /// </summary>
    internal sealed class ProtocolException : Exception
    {
        public ProtocolException(string message)
            : base(message)
        {
        }

        public ProtocolException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
