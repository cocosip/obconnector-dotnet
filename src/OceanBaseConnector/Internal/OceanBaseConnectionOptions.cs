using System;
using System.Globalization;

namespace OceanBaseConnector.Internal
{
    /// <summary>
    /// Immutable connection options frozen from an
    /// <see cref="OceanBaseConnectionStringBuilder"/> via <c>Build()</c>.
    /// Parsed once per connection attempt; <see cref="ToString"/> and the
    /// sanitized connection string never contain the password.
    /// </summary>
    internal sealed class OceanBaseConnectionOptions
    {
        public OceanBaseConnectionOptions(
            string server,
            int port,
            string userId,
            string password,
            string database,
            int connectionTimeoutSeconds,
            int commandTimeoutSeconds,
            OceanBaseTlsMode tlsMode,
            string tlsServerName,
            bool persistSecurityInfo,
            OceanBaseCompatibilityMode? expectedMode,
            bool pooling,
            string sanitizedConnectionString)
        {
            Server = server;
            Port = port;
            UserId = userId;
            Password = password;
            Database = database;
            ConnectionTimeoutSeconds = connectionTimeoutSeconds;
            CommandTimeoutSeconds = commandTimeoutSeconds;
            TlsMode = tlsMode;
            TlsServerName = tlsServerName;
            PersistSecurityInfo = persistSecurityInfo;
            ExpectedMode = expectedMode;
            Pooling = pooling;
            SanitizedConnectionString = sanitizedConnectionString;
        }

        public string Server { get; }

        public int Port { get; }

        public string UserId { get; }

        public string Password { get; }

        public string Database { get; }

        public int ConnectionTimeoutSeconds { get; }

        public int CommandTimeoutSeconds { get; }

        public OceanBaseTlsMode TlsMode { get; }

        public string TlsServerName { get; }

        public bool PersistSecurityInfo { get; }

        public OceanBaseCompatibilityMode? ExpectedMode { get; }

        public bool Pooling { get; }

        /// <summary>Connection string without the password; safe for logs and exceptions.</summary>
        public string SanitizedConnectionString { get; }

        /// <summary>Endpoint description without credentials, for error reporting.</summary>
        public string Endpoint => string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1}",
            Server,
            Port);

        public override string ToString() => SanitizedConnectionString;
    }
}
