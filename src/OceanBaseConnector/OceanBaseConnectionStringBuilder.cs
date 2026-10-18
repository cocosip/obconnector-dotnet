using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using OceanBaseConnector.Internal;

namespace OceanBaseConnector
{
    /// <summary>
    /// Parses and serializes OceanBase connection strings. Canonical keys are
    /// <c>Server</c>, <c>Port</c>, <c>User ID</c>, <c>Password</c>,
    /// <c>Database</c>, <c>Connection Timeout</c>, <c>Command Timeout</c>,
    /// <c>TLS Mode</c>, <c>TLS Server Name</c>, <c>Persist Security Info</c>,
    /// <c>ExpectedMode</c> and <c>Pooling</c>; the only synonyms are
    /// <c>Host</c>, <c>Uid</c> and <c>Pwd</c>. Unknown keys, synonym conflicts
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
        public const string PersistSecurityInfoKey = "Persist Security Info";
        public const string ExpectedModeKey = "ExpectedMode";
        public const string PoolingKey = "Pooling";

        public const int DefaultPort = 2881;
        public const int DefaultConnectionTimeoutSeconds = 15;
        public const int DefaultCommandTimeoutSeconds = 30;

        private static readonly Dictionary<string, string> Synonyms =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = ServerKey,
                ["Uid"] = UserIdKey,
                ["Pwd"] = PasswordKey,
            };

        private static readonly HashSet<string> SecretKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PasswordKey };

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
                PersistSecurityInfo,
                ExpectedMode,
                Pooling,
                ToSanitizedConnectionString(includeSecrets: false));
        }

        // Serialization order for sanitized output; also the complete public key list.
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
            PersistSecurityInfoKey,
            ExpectedModeKey,
            PoolingKey,
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
                if (!includeSecrets && SecretKeys.Contains(key))
                {
                    continue;
                }

                if (base.TryGetValue(key, out var value) && value is string text)
                {
                    serializer[key] = text;
                }
            }

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
                    ParseNonNegativeInt32(canonicalKey, value);
                    return value;

                case TlsModeKey:
                    ParseEnumValue<OceanBaseTlsMode>(canonicalKey, value);
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

                case PersistSecurityInfoKey:
                case PoolingKey:
                    ParseBooleanValue(canonicalKey, value);
                    return value;

                case ExpectedModeKey:
                    ParseEnumValue<OceanBaseCompatibilityMode>(canonicalKey, value);
                    return value;

                default:
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
                    includeValue: !SecretKeys.Contains(canonicalKey),
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
                case "PERSIST SECURITY INFO":
                    canonical = PersistSecurityInfoKey;
                    return true;
                case "EXPECTEDMODE":
                    canonical = ExpectedModeKey;
                    return true;
                case "POOLING":
                    canonical = PoolingKey;
                    return true;
                default:
                    if (Synonyms.TryGetValue(keyword.Trim(), out var synonymCanonical))
                    {
                        canonical = synonymCanonical;
                        return true;
                    }

                    canonical = keyword;
                    return false;
            }
        }

        private static ArgumentException CreateKeywordError(string message, string keyword, bool includeValue, string? value)
        {
            if (includeValue && value is not null && !SecretKeys.Contains(keyword))
            {
                message = $"{message} (value: '{value}')";
            }

            return new ArgumentException(message, nameof(keyword));
        }
    }
}
