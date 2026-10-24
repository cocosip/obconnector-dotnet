using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector
{
    /// <summary>
    /// Parses and serializes OceanBase connection strings. Canonical keys are
    /// <c>Server</c>, <c>Port</c>, <c>User ID</c>, <c>Password</c>,
    /// <c>Database</c>, <c>Connection Timeout</c>, <c>Command Timeout</c>,
    /// <c>TLS Mode</c>, <c>TLS Server Name</c>, <c>Persist Security Info</c>,
    /// <c>ExpectedMode</c>, <c>Pooling</c>, pool sizing/lifetime options,
    /// <c>Server RSA Public Key</c> and
    /// <c>Allow Public Key Retrieval</c>, <c>Use OB20</c>, <c>Enable Full Link Trace</c> and
    /// <c>Use Oracle Prepare Execute</c>, <c>Use Lob Locators</c>, <c>Allow Multi Statements</c>, <c>Allow Local Infile</c>, <c>Check Certificate Revocation</c>, <c>Load Balance</c>, <c>Service Principal Name</c>,
    /// <c>Treat Tiny As Boolean</c> and <c>Use Affected Rows</c>; the only synonyms are
    /// <c>Host</c>, <c>Uid</c> and <c>Pwd</c>. PAM accepts secret <c>PasswordN</c> keys for N >= 2.
    /// Unknown keys, synonym conflicts
    /// and malformed values are rejected instead of being silently accepted.
    /// </summary>
    public sealed class OceanBaseConnectionStringBuilder : DbConnectionStringBuilder
    {
        public const string ServerKey = "Server";
        public const string PortKey = "Port";
        public const string UserIdKey = "User ID";
        public const string PasswordKey = "Password";
        public const string DatabaseKey = "Database";
        public const string ConnectionTimeoutKey = "Connection Timeout";
        public const string CommandTimeoutKey = "Command Timeout";
        public const string TlsModeKey = "TLS Mode";
        public const string TlsServerNameKey = "TLS Server Name";
        public const string CheckCertificateRevocationKey = "Check Certificate Revocation";
        public const string PersistSecurityInfoKey = "Persist Security Info";
        public const string ExpectedModeKey = "ExpectedMode";
        public const string PoolingKey = "Pooling";
        public const string MinPoolSizeKey = "Min Pool Size";
        public const string MaxPoolSizeKey = "Max Pool Size";
        public const string PoolWaitTimeoutKey = "Pool Wait Timeout";
        public const string ConnectionIdleLifetimeKey = "Connection Idle Lifetime";
        public const string ConnectionLifetimeKey = "Connection Lifetime";
        public const string ServerRsaPublicKeyKey = "Server RSA Public Key";
        public const string AllowPublicKeyRetrievalKey = "Allow Public Key Retrieval";
        public const string UseOb20Key = "Use OB20";
        public const string UseCompressionKey = "Use Compression";
        public const string EnableFullLinkTraceKey = "Enable Full Link Trace";
        public const string UseOraclePrepareExecuteKey = "Use Oracle Prepare Execute";
        public const string UseLobLocatorsKey = "Use Lob Locators";
        public const string AllowMultiStatementsKey = "Allow Multi Statements";
        public const string AllowLocalInfileKey = "Allow Local Infile";
        public const string LoadBalanceKey = "Load Balance";
        public const string ServicePrincipalNameKey = "Service Principal Name";
        public const string TreatTinyAsBooleanKey = "Treat Tiny As Boolean";
        public const string UseAffectedRowsKey = "Use Affected Rows";

        public const int DefaultPort = 2881;
        public const int DefaultConnectionTimeoutSeconds = 15;
        public const int DefaultCommandTimeoutSeconds = 30;
        public const int DefaultMaxPoolSize = 100;

        private static readonly Dictionary<string, string> Synonyms =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = ServerKey,
                ["Uid"] = UserIdKey,
                ["Pwd"] = PasswordKey,
            };

        private static readonly HashSet<string> SecretKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PasswordKey, ServerRsaPublicKeyKey };

        // Canonical key -> raw keyword last used to set it; detects conflicts
        // such as "Server=a;Host=b" even when the values are identical.
        private readonly Dictionary<string, string> _rawKeywordByCanonical =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public OceanBaseConnectionStringBuilder()
        {
        }

        public OceanBaseConnectionStringBuilder(string connectionString)
        {
            ConnectionString = connectionString;
        }

        /// <summary>Comma-separated host[:port] or [IPv6][:port] candidates; omitted ports use Port.</summary>
        public string Server
        {
            get => GetString(ServerKey) ?? string.Empty;
            set => this[ServerKey] = value;
        }

        public int Port
        {
            get => GetInt32(PortKey) ?? DefaultPort;
            set => this[PortKey] = value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Endpoint order for each Open; defaults to Sequential.</summary>
        public OceanBaseLoadBalance LoadBalance
        {
            get => GetString(LoadBalanceKey) is string value ? ParseEnumValue<OceanBaseLoadBalance>(LoadBalanceKey, value) : OceanBaseLoadBalance.Sequential;
            set => this[LoadBalanceKey] = value.ToString();
        }

        public string UserId
        {
            get => GetString(UserIdKey) ?? string.Empty;
            set => this[UserIdKey] = value;
        }

        public string Password
        {
            get => GetString(PasswordKey) ?? string.Empty;
            set => this[PasswordKey] = value;
        }

        public string Database
        {
            get => GetString(DatabaseKey) ?? string.Empty;
            set => this[DatabaseKey] = value;
        }

        public int ConnectionTimeout
        {
            get => GetInt32(ConnectionTimeoutKey) ?? DefaultConnectionTimeoutSeconds;
            set => this[ConnectionTimeoutKey] = value.ToString(CultureInfo.InvariantCulture);
        }

        public int CommandTimeout
        {
            get => GetInt32(CommandTimeoutKey) ?? DefaultCommandTimeoutSeconds;
            set => this[CommandTimeoutKey] = value.ToString(CultureInfo.InvariantCulture);
        }

        public OceanBaseTlsMode TlsMode
        {
            get
            {
                var value = GetString(TlsModeKey);
                return value is null ? OceanBaseTlsMode.VerifyFull : ParseEnumValue<OceanBaseTlsMode>(TlsModeKey, value);
            }
            set => this[TlsModeKey] = value.ToString();
        }

        public string TlsServerName
        {
            get => GetString(TlsServerNameKey) ?? string.Empty;
            set => this[TlsServerNameKey] = value;
        }

        /// <summary>Enable platform certificate revocation checks during verified TLS; disabled by default.</summary>
        public bool CheckCertificateRevocation
        {
            get => GetBoolean(CheckCertificateRevocationKey) ?? false;
            set => this[CheckCertificateRevocationKey] = value ? "true" : "false";
        }

        public bool PersistSecurityInfo
        {
            get => GetBoolean(PersistSecurityInfoKey) ?? false;
            set => this[PersistSecurityInfoKey] = value ? "true" : "false";
        }

        public OceanBaseCompatibilityMode? ExpectedMode
        {
            get
            {
                var value = GetString(ExpectedModeKey);
                return value is null ? (OceanBaseCompatibilityMode?)null : ParseEnumValue<OceanBaseCompatibilityMode>(ExpectedModeKey, value);
            }
            set
            {
                if (value.HasValue)
                {
                    this[ExpectedModeKey] = value.Value.ToString();
                }
                else
                {
                    Remove(ExpectedModeKey);
                }
            }
        }

        public bool Pooling
        {
            get => GetBoolean(PoolingKey) ?? false;
            set => this[PoolingKey] = value ? "true" : "false";
        }

        public int MinPoolSize
        {
            get => GetInt32(MinPoolSizeKey) ?? 0;
            set => this[MinPoolSizeKey] = value.ToString(CultureInfo.InvariantCulture);
        }

        public int MaxPoolSize
        {
            get => GetInt32(MaxPoolSizeKey) ?? DefaultMaxPoolSize;
            set => this[MaxPoolSizeKey] = value.ToString(CultureInfo.InvariantCulture);
        }

        public int PoolWaitTimeout
        {
            get => GetInt32(PoolWaitTimeoutKey) ?? ConnectionTimeout;
            set => this[PoolWaitTimeoutKey] = value.ToString(CultureInfo.InvariantCulture);
        }

        public int ConnectionIdleLifetime
        {
            get => GetInt32(ConnectionIdleLifetimeKey) ?? 0;
            set => this[ConnectionIdleLifetimeKey] = value.ToString(CultureInfo.InvariantCulture);
        }

        public int ConnectionLifetime
        {
            get => GetInt32(ConnectionLifetimeKey) ?? 0;
            set => this[ConnectionLifetimeKey] = value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Trusted RSA SubjectPublicKeyInfo PEM for SHA-2 authentication without verified TLS.</summary>
        public string ServerRsaPublicKey
        {
            get => GetString(ServerRsaPublicKeyKey) ?? string.Empty;
            set => this[ServerRsaPublicKeyKey] = value;
        }

        /// <summary>Explicitly allow unauthenticated server RSA key retrieval; disabled by default.</summary>
        public bool AllowPublicKeyRetrieval
        {
            get => GetBoolean(AllowPublicKeyRetrievalKey) ?? false;
            set => this[AllowPublicKeyRetrievalKey] = value ? "true" : "false";
        }

        /// <summary>Require the standard MySQL zlib compression protocol. Default false.</summary>
        public bool UseCompression
        {
            get => GetBoolean(UseCompressionKey) ?? false;
            set => this[UseCompressionKey] = value ? "true" : "false";
        }

        /// <summary>Request OB20 framing when confirmed by server negotiation; disabled by default.</summary>
        public bool UseOb20
        {
            get => GetBoolean(UseOb20Key) ?? false;
            set => this[UseOb20Key] = value ? "true" : "false";
        }

        /// <summary>Request server full-link tracing over OB20; disabled by default.</summary>
        public bool EnableFullLinkTrace
        {
            get => GetBoolean(EnableFullLinkTraceKey) ?? false;
            set => this[EnableFullLinkTraceKey] = value ? "true" : "false";
        }

        /// <summary>Enables Oracle procedures and output bindings using PrepareExecute after version discovery.</summary>
        public bool UseOraclePrepareExecute
        {
            get => GetBoolean(UseOraclePrepareExecuteKey) ?? false;
            set => this[UseOraclePrepareExecuteKey] = value ? "true" : "false";
        }

        /// <summary>Request Oracle LOB locators; requires Oracle PrepareExecute for external reads.</summary>
        public bool UseLobLocators
        {
            get => GetBoolean(UseLobLocatorsKey) ?? false;
            set => this[UseLobLocatorsKey] = value ? "true" : "false";
        }

        /// <summary>Opt in to MySQL multi-statement text execution; disabled by default.</summary>
        public bool AllowMultiStatements
        {
            get => GetBoolean(AllowMultiStatementsKey) ?? false;
            set => this[AllowMultiStatementsKey] = value ? "true" : "false";
        }

        /// <summary>Opt in to server-requested LOCAL INFILE uploads backed by the connection's stream factory.</summary>
        public bool AllowLocalInfile
        {
            get => GetBoolean(AllowLocalInfileKey) ?? false;
            set => this[AllowLocalInfileKey] = value ? "true" : "false";
        }

        /// <summary>Optional trusted Kerberos service principal overriding the server's GSSAPI challenge.</summary>
        public string ServicePrincipalName
        {
            get => GetString(ServicePrincipalNameKey) ?? string.Empty;
            set => this[ServicePrincipalNameKey] = value;
        }

        /// <summary>Map signed MySQL TINYINT(1) columns to Boolean; enabled by default.</summary>
        public bool TreatTinyAsBoolean
        {
            get => GetBoolean(TreatTinyAsBooleanKey) ?? true;
            set => this[TreatTinyAsBooleanKey] = value ? "true" : "false";
        }

        /// <summary>Request changed-row counts instead of matched-row counts; disabled by default.</summary>
        public bool UseAffectedRows
        {
            get => GetBoolean(UseAffectedRowsKey) ?? false;
            set => this[UseAffectedRowsKey] = value ? "true" : "false";
        }

        public override object? this[string keyword]
        {
            get
            {
                var canonical = RequireCanonicalKeyword(keyword);
                // Missing keys read as null; the base getter throws instead.
                return base.TryGetValue(canonical, out var value) ? value : null;
            }

            set
            {
                var canonical = RequireCanonicalKeyword(keyword);

                // Null values remove the key (ADO.NET convention).
                if (value is null)
                {
                    Remove(canonical);
                    return;
                }

                var stringValue = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                var normalizedKeyword = keyword.Trim();

                if (_rawKeywordByCanonical.TryGetValue(canonical, out var rawKeyword) &&
                    !string.Equals(rawKeyword, normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    throw CreateKeywordError(
                        $"Conflicting connection string keywords '{rawKeyword}' and '{normalizedKeyword}' both map to '{canonical}'.",
                        normalizedKeyword,
                        includeValue: false,
                        value: null);
                }

                var validated = ValidateValue(canonical, stringValue);
                base[canonical] = validated;
                _rawKeywordByCanonical[canonical] = normalizedKeyword;
            }
        }

        public override void Clear()
        {
            base.Clear();
            _rawKeywordByCanonical.Clear();
        }

        public override bool Remove(string keyword)
        {
            if (string.IsNullOrEmpty(keyword) ||
                !TryCanonicalize(keyword, out var canonical))
            {
                return false;
            }

            var removed = base.Remove(canonical);
            _rawKeywordByCanonical.Remove(canonical);
            return removed;
        }

        /// <summary>
        /// Parses the current state once into an immutable options snapshot;
        /// later builder mutations do not affect the returned object.
        /// </summary>
        internal OceanBaseConnectionOptions Build()
        {
            if (Server.Length == 0)
            {
                throw CreateKeywordError($"'{ServerKey}' is required.", ServerKey, includeValue: false, value: null);
            }

            if (UserId.Length == 0)
            {
                throw CreateKeywordError($"'{UserIdKey}' is required.", UserIdKey, includeValue: false, value: null);
            }

            if (UseCompression && UseOb20) throw new ArgumentException("Use Compression and Use OB20 cannot be combined; compressed OB20 is not supported yet.");
            if (EnableFullLinkTrace && !UseOb20) throw new ArgumentException("Full-link tracing requires Use OB20=true.");
            if (UseLobLocators && !UseOraclePrepareExecute) throw new ArgumentException("LOB locators require Use Oracle Prepare Execute=true.");
            if (MaxPoolSize < 1)
                throw new ArgumentException($"'{MaxPoolSizeKey}' must be at least 1.");
            if (MinPoolSize > MaxPoolSize)
                throw new ArgumentException($"'{MinPoolSizeKey}' cannot exceed '{MaxPoolSizeKey}'.");
            var additionalPasswords = new Dictionary<int, string>();
            foreach (string key in Keys)
                if (TryGetPasswordNumber(key, out var number)) additionalPasswords.Add(number, GetString(key)!);

            return new OceanBaseConnectionOptions(
                Server,
                Port,
                UserId,
                Password,
                Database,
                ConnectionTimeout,
                CommandTimeout,
                TlsMode,
                TlsServerName,
                CheckCertificateRevocation,
                PersistSecurityInfo,
                ExpectedMode,
                Pooling,
                MinPoolSize,
                MaxPoolSize,
                PoolWaitTimeout,
                ConnectionIdleLifetime,
                ConnectionLifetime,
                ServerRsaPublicKey,
                AllowPublicKeyRetrieval,
                UseOb20,
                EnableFullLinkTrace,
                UseOraclePrepareExecute,
                UseLobLocators,
                AllowMultiStatements,
                AllowLocalInfile,
                LoadBalance,
                ToSanitizedConnectionString(includeSecrets: false),
                additionalPasswords,
                ServicePrincipalName,
                TreatTinyAsBoolean,
                UseAffectedRows,
                UseCompression);
        }

        // Serialization order for fixed keys; additional PAM passwords are secrets.
        private static readonly string[] AllCanonicalKeys =
        {
            ServerKey,
            PortKey,
            UserIdKey,
            PasswordKey,
            DatabaseKey,
            ConnectionTimeoutKey,
            CommandTimeoutKey,
            TlsModeKey,
            TlsServerNameKey,
            CheckCertificateRevocationKey,
            PersistSecurityInfoKey,
            ExpectedModeKey,
            PoolingKey,
            MinPoolSizeKey,
            MaxPoolSizeKey,
            PoolWaitTimeoutKey,
            ConnectionIdleLifetimeKey,
            ConnectionLifetimeKey,
            ServerRsaPublicKeyKey,
            AllowPublicKeyRetrievalKey,
            UseOb20Key,
            UseCompressionKey,
            EnableFullLinkTraceKey,
            UseOraclePrepareExecuteKey,
            UseLobLocatorsKey,
            AllowMultiStatementsKey,
            AllowLocalInfileKey,
            LoadBalanceKey,
            ServicePrincipalNameKey,
            TreatTinyAsBooleanKey,
            UseAffectedRowsKey,
        };

        /// <summary>
        /// Serializes the current state, optionally including the password.
        /// The builder must be able to serialize secrets so connections can be
        /// created from it; callers must protect that output. Everything the
        /// driver itself reports uses the sanitized form.
        /// </summary>
        public string ToSanitizedConnectionString(bool includeSecrets)
        {
            var serializer = new DbConnectionStringBuilder();
            foreach (var key in AllCanonicalKeys)
            {
                if (!includeSecrets && IsSecretKey(key))
                {
                    continue;
                }

                if (base.TryGetValue(key, out var value) && value is string text)
                {
                    serializer[key] = text;
                }
            }

            if (includeSecrets)
                foreach (string key in Keys)
                    if (TryGetPasswordNumber(key, out _)) serializer[key] = base[key];

            return serializer.ConnectionString;
        }

        private string? GetString(string canonicalKey)
        {
            return base.TryGetValue(canonicalKey, out var value) ? value as string : null;
        }

        private int? GetInt32(string canonicalKey)
        {
            var value = GetString(canonicalKey);
            return value is null ? (int?)null : ParseNonNegativeInt32(canonicalKey, value);
        }

        private bool? GetBoolean(string canonicalKey)
        {
            var value = GetString(canonicalKey);
            return value is null ? (bool?)null : ParseBooleanValue(canonicalKey, value);
        }

        private string ValidateValue(string canonicalKey, string value)
        {
            switch (canonicalKey)
            {
                case ServerKey:
                    OceanBaseEndpoint.Parse(value, DefaultPort);
                    return value;

                case UserIdKey:
                    if (value.Trim().Length == 0)
                    {
                        throw CreateKeywordError($"'{canonicalKey}' must not be empty.", canonicalKey, includeValue: false, value: null);
                    }

                    return value;

                case PasswordKey:
                case DatabaseKey:
                    return value;

                case PortKey:
                    var port = ParseNonNegativeInt32(canonicalKey, value);
                    if (port < 1 || port > 65535)
                    {
                        throw CreateKeywordError($"'{canonicalKey}' must be between 1 and 65535.", canonicalKey, includeValue: true, value: value);
                    }

                    return value;

                case ConnectionTimeoutKey:
                case CommandTimeoutKey:
                case MinPoolSizeKey:
                case MaxPoolSizeKey:
                case PoolWaitTimeoutKey:
                case ConnectionIdleLifetimeKey:
                case ConnectionLifetimeKey:
                    var poolValue = ParseNonNegativeInt32(canonicalKey, value);
                    if (canonicalKey == MaxPoolSizeKey && poolValue < 1)
                        throw CreateKeywordError($"'{canonicalKey}' must be at least 1.", canonicalKey, includeValue: true, value: value);
                    return value;

                case TlsModeKey:
                    ParseEnumValue<OceanBaseTlsMode>(canonicalKey, value);
                    return value;

                case LoadBalanceKey:
                    ParseEnumValue<OceanBaseLoadBalance>(canonicalKey, value);
                    return value;

                case TlsServerNameKey:
                    if (value.Trim().Length == 0)
                    {
                        throw CreateKeywordError(
                            $"'{canonicalKey}' must not be empty; remove the keyword to use the endpoint name.",
                            canonicalKey,
                            includeValue: false,
                            value: null);
                    }

                    return value;

                case CheckCertificateRevocationKey:
                    ParseBooleanValue(canonicalKey, value);
                    return value;

                case PersistSecurityInfoKey:
                case PoolingKey:
                case AllowPublicKeyRetrievalKey:
                case UseCompressionKey:
                case UseOb20Key:
                case EnableFullLinkTraceKey:
                case UseOraclePrepareExecuteKey:
                case UseLobLocatorsKey:
                case AllowMultiStatementsKey:
                case AllowLocalInfileKey:
                case TreatTinyAsBooleanKey:
                case UseAffectedRowsKey:
                    ParseBooleanValue(canonicalKey, value);
                    return value;

                case ServicePrincipalNameKey:
                    if (value.Length != 0 && !GssapiChallenge.IsValidPrincipal(value))
                        throw CreateKeywordError("Service principal name must be valid Unicode, at most 1024 characters, without control characters.", canonicalKey, includeValue: false, value: null);
                    return value;

                case ServerRsaPublicKeyKey:
                    if (value.Length > 16384)
                        throw CreateKeywordError("RSA public key exceeds 16 KiB.", canonicalKey, includeValue: false, value: null);
                    return value;

                case ExpectedModeKey:
                    ParseEnumValue<OceanBaseCompatibilityMode>(canonicalKey, value);
                    return value;

                default:
                    if (TryGetPasswordNumber(canonicalKey, out _)) return value;
                    throw CreateKeywordError($"Unknown connection string keyword '{canonicalKey}'.", canonicalKey, includeValue: false, value: null);
            }
        }

        private static int ParseNonNegativeInt32(string canonicalKey, string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                throw CreateKeywordError(
                    $"'{canonicalKey}' must be a non-negative integer.",
                    canonicalKey,
                    includeValue: !IsSecretKey(canonicalKey),
                    value: value);
            }

            return parsed;
        }

        private static bool ParseBooleanValue(string canonicalKey, string value)
        {
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw CreateKeywordError($"'{canonicalKey}' must be 'true' or 'false'.", canonicalKey, includeValue: true, value: value);
        }

        private static T ParseEnumValue<T>(string canonicalKey, string value)
            where T : struct, Enum
        {
            // Match by name only: numeric strings must not be accepted.
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                {
                    return (T)Enum.Parse(typeof(T), name, ignoreCase: false);
                }
            }

            throw CreateKeywordError(
                $"'{canonicalKey}' must be one of: {string.Join(", ", Enum.GetNames(typeof(T)))}.",
                canonicalKey,
                includeValue: true,
                value: value);
        }

        private string RequireCanonicalKeyword(string keyword)
        {
            if (string.IsNullOrEmpty(keyword))
            {
                throw new ArgumentException("The connection string keyword must not be null or empty.", nameof(keyword));
            }

            if (!TryCanonicalize(keyword, out var canonical))
            {
                throw CreateKeywordError($"Unknown connection string keyword '{keyword}'.", keyword, includeValue: false, value: null);
            }

            return canonical;
        }

        private static bool TryCanonicalize(string keyword, out string canonical)
        {
            switch (keyword.Trim().ToUpperInvariant())
            {
                case "TREAT TINY AS BOOLEAN":
                    canonical = TreatTinyAsBooleanKey;
                    return true;
                case "USE AFFECTED ROWS":
                    canonical = UseAffectedRowsKey;
                    return true;
                case "ALLOW MULTI STATEMENTS":
                    canonical = AllowMultiStatementsKey;
                    return true;
                case "ALLOW LOCAL INFILE":
                    canonical = AllowLocalInfileKey;
                    return true;
                case "SERVER":
                    canonical = ServerKey;
                    return true;
                case "PORT":
                    canonical = PortKey;
                    return true;
                case "USER ID":
                    canonical = UserIdKey;
                    return true;
                case "PASSWORD":
                    canonical = PasswordKey;
                    return true;
                case "DATABASE":
                    canonical = DatabaseKey;
                    return true;
                case "CONNECTION TIMEOUT":
                    canonical = ConnectionTimeoutKey;
                    return true;
                case "COMMAND TIMEOUT":
                    canonical = CommandTimeoutKey;
                    return true;
                case "TLS MODE":
                    canonical = TlsModeKey;
                    return true;
                case "TLS SERVER NAME":
                    canonical = TlsServerNameKey;
                    return true;
                case "CHECK CERTIFICATE REVOCATION":
                    canonical = CheckCertificateRevocationKey;
                    return true;
                case "PERSIST SECURITY INFO":
                    canonical = PersistSecurityInfoKey;
                    return true;
                case "EXPECTEDMODE":
                    canonical = ExpectedModeKey;
                    return true;
                case "POOLING":
                    canonical = PoolingKey;
                    return true;
                case "MIN POOL SIZE":
                    canonical = MinPoolSizeKey;
                    return true;
                case "MAX POOL SIZE":
                    canonical = MaxPoolSizeKey;
                    return true;
                case "POOL WAIT TIMEOUT":
                    canonical = PoolWaitTimeoutKey;
                    return true;
                case "CONNECTION IDLE LIFETIME":
                    canonical = ConnectionIdleLifetimeKey;
                    return true;
                case "CONNECTION LIFETIME":
                    canonical = ConnectionLifetimeKey;
                    return true;
                case "SERVER RSA PUBLIC KEY":
                    canonical = ServerRsaPublicKeyKey;
                    return true;
                case "ALLOW PUBLIC KEY RETRIEVAL":
                    canonical = AllowPublicKeyRetrievalKey;
                    return true;
                case "USE COMPRESSION":
                    canonical = UseCompressionKey;
                    return true;
                case "USE OB20":
                    canonical = UseOb20Key;
                    return true;
                case "ENABLE FULL LINK TRACE":
                    canonical = EnableFullLinkTraceKey;
                    return true;
                case "USE ORACLE PREPARE EXECUTE":
                    canonical = UseOraclePrepareExecuteKey;
                    return true;
                case "USE LOB LOCATORS":
                    canonical = UseLobLocatorsKey;
                    return true;
                case "SERVICE PRINCIPAL NAME":
                    canonical = ServicePrincipalNameKey;
                    return true;
                case "LOAD BALANCE":
                    canonical = LoadBalanceKey;
                    return true;
                default:
                    if (TryGetPasswordNumber(keyword.Trim(), out var number))
                    {
                        canonical = PasswordKey + number.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                    if (Synonyms.TryGetValue(keyword.Trim(), out var synonymCanonical))
                    {
                        canonical = synonymCanonical;
                        return true;
                    }

                    canonical = keyword;
                    return false;
            }
        }

        private static bool TryGetPasswordNumber(string keyword, out int number)
        {
            number = 0;
            if (!keyword.StartsWith(PasswordKey, StringComparison.OrdinalIgnoreCase) || keyword.Length <= PasswordKey.Length)
                return false;
            var suffix = keyword.Substring(PasswordKey.Length);
            return suffix[0] != '0' && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 2;
        }

        private static bool IsSecretKey(string keyword) => SecretKeys.Contains(keyword) || TryGetPasswordNumber(keyword, out _);

        private static ArgumentException CreateKeywordError(string message, string keyword, bool includeValue, string? value)
        {
            if (includeValue && value is not null && !IsSecretKey(keyword))
            {
                message = $"{message} (value: '{value}')";
            }

            return new ArgumentException(message, nameof(keyword));
        }
    }
}
