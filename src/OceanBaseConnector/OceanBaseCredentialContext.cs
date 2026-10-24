using OceanBaseConnector.Internal;

namespace OceanBaseConnector
{
    /// <summary>Configured logical target, before address discovery. Contains no password.</summary>
    public sealed class OceanBaseCredentialContext
    {
        internal OceanBaseCredentialContext(OceanBaseConnectionOptions options)
        { Server = options.Server; Port = options.Port; UserId = options.UserId; Database = options.Database; }
        public string Server { get; }
        public int Port { get; }
        public string UserId { get; }
        public string Database { get; }
    }
}
