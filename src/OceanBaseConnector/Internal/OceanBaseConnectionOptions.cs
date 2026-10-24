using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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
            bool checkCertificateRevocation,
            bool persistSecurityInfo,
            OceanBaseCompatibilityMode? expectedMode,
            bool pooling,
            int minPoolSize,
            int maxPoolSize,
            int poolWaitTimeoutSeconds,
            int connectionIdleLifetimeSeconds,
            int connectionLifetimeSeconds,
            string serverRsaPublicKey,
            bool allowPublicKeyRetrieval,
            bool useOb20,
            bool enableFullLinkTrace,
            bool useOraclePrepareExecute,
            bool useLobLocators,
            bool allowMultiStatements,
            bool allowLocalInfile,
            OceanBaseLoadBalance loadBalance,
            string sanitizedConnectionString,
            IReadOnlyDictionary<int, string>? additionalPasswords = null,
            string servicePrincipalName = "",
            bool treatTinyAsBoolean = true,
            bool useAffectedRows = false)
        {
            Server = server;
            Port = port;
            Endpoints = Array.AsReadOnly(OceanBaseEndpoint.Parse(server, port));
            UserId = userId;
            Password = password;
            ServicePrincipalName = servicePrincipalName;
            TreatTinyAsBoolean = treatTinyAsBoolean;
            UseAffectedRows = useAffectedRows;
            AllowMultiStatements = allowMultiStatements;
            AllowLocalInfile = allowLocalInfile;
            var passwords = new Dictionary<int, string>();
            if (additionalPasswords is not null)
                foreach (var entry in additionalPasswords) passwords.Add(entry.Key, entry.Value);
            AdditionalPasswords = new ReadOnlyDictionary<int, string>(passwords);
            Database = database;
            ConnectionTimeoutSeconds = connectionTimeoutSeconds;
            CommandTimeoutSeconds = commandTimeoutSeconds;
            TlsMode = tlsMode;
            TlsServerName = tlsServerName;
            CheckCertificateRevocation = checkCertificateRevocation;
            PersistSecurityInfo = persistSecurityInfo;
            ExpectedMode = expectedMode;
            Pooling = pooling;
            MinPoolSize = minPoolSize;
            MaxPoolSize = maxPoolSize;
            PoolWaitTimeoutSeconds = poolWaitTimeoutSeconds;
            ConnectionIdleLifetimeSeconds = connectionIdleLifetimeSeconds;
            ConnectionLifetimeSeconds = connectionLifetimeSeconds;
            ServerRsaPublicKey = serverRsaPublicKey;
            AllowPublicKeyRetrieval = allowPublicKeyRetrieval;
            UseOb20 = useOb20;
            EnableFullLinkTrace = enableFullLinkTrace;
            UseOraclePrepareExecute = useOraclePrepareExecute;
            UseLobLocators = useLobLocators;
            LoadBalance = loadBalance;
            SanitizedConnectionString = sanitizedConnectionString;
        }

        public string Server { get; }

        public int Port { get; }

        internal IReadOnlyList<OceanBaseEndpoint> Endpoints { get; }

        internal OceanBaseConnectionOptions ForEndpoint(OceanBaseEndpoint endpoint) => new OceanBaseConnectionOptions(
            endpoint.Host, endpoint.Port, UserId, Password, Database, ConnectionTimeoutSeconds, CommandTimeoutSeconds,
            TlsMode, TlsServerName, CheckCertificateRevocation, PersistSecurityInfo, ExpectedMode, Pooling, MinPoolSize, MaxPoolSize, PoolWaitTimeoutSeconds, ConnectionIdleLifetimeSeconds, ConnectionLifetimeSeconds, ServerRsaPublicKey, AllowPublicKeyRetrieval, UseOb20, EnableFullLinkTrace, UseOraclePrepareExecute, UseLobLocators, AllowMultiStatements, AllowLocalInfile, LoadBalance, SanitizedConnectionString, AdditionalPasswords, ServicePrincipalName, TreatTinyAsBoolean, UseAffectedRows);

        public string UserId { get; }

        public string Password { get; }

        internal IReadOnlyDictionary<int, string> AdditionalPasswords { get; }

        internal string ServicePrincipalName { get; }

        public string Database { get; }

        public int ConnectionTimeoutSeconds { get; }

        public int CommandTimeoutSeconds { get; }

        public OceanBaseTlsMode TlsMode { get; }

        public string TlsServerName { get; }

        public bool CheckCertificateRevocation { get; }

        public bool PersistSecurityInfo { get; }

        public OceanBaseCompatibilityMode? ExpectedMode { get; }

        public bool Pooling { get; }
        public int MinPoolSize { get; }
        public int MaxPoolSize { get; }
        public int PoolWaitTimeoutSeconds { get; }
        public int ConnectionIdleLifetimeSeconds { get; }
        public int ConnectionLifetimeSeconds { get; }
        public bool TreatTinyAsBoolean { get; }
        public bool UseAffectedRows { get; }
        public bool AllowMultiStatements { get; }
        public bool AllowLocalInfile { get; }

        public string ServerRsaPublicKey { get; }

        public bool AllowPublicKeyRetrieval { get; }

        public bool UseOb20 { get; }
        public bool EnableFullLinkTrace { get; }
        public bool UseOraclePrepareExecute { get; }
        public bool UseLobLocators { get; }
        public OceanBaseLoadBalance LoadBalance { get; }

        /// <summary>Connection string without the password; safe for logs and exceptions.</summary>
        public string SanitizedConnectionString { get; }

        /// <summary>Endpoint description without credentials, for error reporting.</summary>
        public string Endpoint => string.Join(",", Endpoints);

        public override string ToString() => SanitizedConnectionString;
    }
}
