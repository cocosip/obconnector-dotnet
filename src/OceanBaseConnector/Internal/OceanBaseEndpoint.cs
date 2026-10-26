using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace OceanBaseConnector.Internal
{
    internal sealed class OceanBaseEndpoint
    {
        private OceanBaseEndpoint(string host, int port, DiscoveryBlacklistPolicy? blacklist = null) { Host = host; Port = port; BlacklistPolicy = blacklist; }

        internal string Host { get; }
        internal int Port { get; }
        internal DiscoveryBlacklistPolicy? BlacklistPolicy { get; }
        internal OceanBaseEndpoint WithBlacklist(DiscoveryBlacklistPolicy policy) => new OceanBaseEndpoint(Host, Port, policy);
        public override string ToString() => (Host.IndexOf(':') >= 0 ? "[" + Host + "]" : Host) +
            ":" + Port.ToString(CultureInfo.InvariantCulture);

        internal static OceanBaseEndpoint[] Parse(string servers, int defaultPort)
        {
            var entries = servers.Split(',');
            var result = new OceanBaseEndpoint[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i].Trim();
                string host;
                string? portText = null;
                if (entry.StartsWith("[", StringComparison.Ordinal))
                {
                    var end = entry.IndexOf(']');
                    if (end < 2) throw InvalidAddress();
                    host = entry.Substring(1, end - 1);
                    if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
                        throw InvalidAddress();
                    if (end + 1 < entry.Length)
                    {
                        if (entry[end + 1] != ':') throw InvalidAddress();
                        portText = entry.Substring(end + 2);
                    }
                }
                else
                {
                    var colon = entry.IndexOf(':');
                    if (colon >= 0 && colon == entry.LastIndexOf(':'))
                    {
                        host = entry.Substring(0, colon);
                        portText = entry.Substring(colon + 1);
                    }
                    else host = entry;
                }

                if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.IndexOf('[') >= 0 || host.IndexOf(']') >= 0)
                    throw InvalidAddress();
                var port = defaultPort;
                if (portText != null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535))
                    throw InvalidAddress();
                result[i] = new OceanBaseEndpoint(host, port);
            }
            return result;
        }

        private static ArgumentException InvalidAddress() => new ArgumentException(
            "Server must contain comma-separated host[:port] or [IPv6][:port] addresses with ports between 1 and 65535.", "Server");
    }
}
