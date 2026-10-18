using System;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    /// <summary>
    /// The three capability states kept separately by design: what the
    /// client requested, what the server offered, and what is actually in
    /// force (requested intersected with the server offer; version and mode
    /// conditions further restrict specific features where they are used).
    /// </summary>
    internal sealed class NegotiatedCapabilities
    {
        public NegotiatedCapabilities(CapabilityFlags requested, CapabilityFlags server)
        {
            Requested = requested;
            Server = server;
        }

        public CapabilityFlags Requested { get; }

        public CapabilityFlags Server { get; }

        public CapabilityFlags Effective => Requested & Server;

        public bool HasEffective(CapabilityFlags flag)
        {
            return (Effective & flag) != 0;
        }
    }
}
