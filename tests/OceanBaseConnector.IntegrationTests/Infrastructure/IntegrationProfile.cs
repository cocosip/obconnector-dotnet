using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

/// <summary>
/// One configured target database. Tests receive the profile name as a theory
/// parameter and open fresh connections from it, so switching databases means
/// editing the profiles file, not the tests.
/// </summary>
public sealed class IntegrationProfile
{
    public string Name { get; init; } = "";
    public OceanBaseCompatibilityMode Mode { get; init; }
    public string ConnectionString { get; init; } = "";
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool CreateObjects { get; init; }
    public string? SlowQuery { get; init; }
    public string? AlternateDatabase { get; init; }
    public string? UnsupportedAuthConnectionString { get; init; }
    public string? AutocommitDisabledConnectionString { get; init; }

    public bool Oracle => Mode == OceanBaseCompatibilityMode.Oracle;
    public bool HasTag(string tag) => Tags.Contains(tag);

    /// <summary>
    /// A builder with the profile's identity, options and validation applied.
    /// Tests clone it before mutating endpoint-scoped settings.
    /// </summary>
    public OceanBaseConnectionStringBuilder CreateBuilder()
    {
        var builder = new OceanBaseConnectionStringBuilder(ConnectionString);
        if (builder.ExpectedMode.HasValue && builder.ExpectedMode != Mode)
            throw new InvalidOperationException($"Profile '{Name}': connection string ExpectedMode conflicts with the configured mode.");
        builder.ExpectedMode = Mode;
        if (builder.ConnectionTimeout <= 0 || builder.CommandTimeout <= 0)
            throw new InvalidOperationException($"Profile '{Name}': integration tests require positive connection and command timeouts.");
        return builder;
    }

    public OceanBaseConnection CreateConnection() => new(CreateBuilder().ConnectionString);
}

/// <summary>Parses the profiles configuration file. Strict: duplicates and unknown members are rejected.</summary>
internal static class IntegrationProfileConfig
{
    public static IReadOnlyList<IntegrationProfile> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Integration configuration must be a JSON object.");
        RejectDuplicates(document.RootElement);
        if (!document.RootElement.TryGetProperty("profiles", out var profilesElement) ||
            profilesElement.ValueKind != JsonValueKind.Array || profilesElement.GetArrayLength() == 0)
            throw new InvalidOperationException("Integration configuration needs a non-empty 'profiles' array.");

        var config = JsonSerializer.Deserialize<ConfigFile>(json, Options)
            ?? throw new InvalidOperationException("Integration configuration must be a JSON object.");
        var profiles = new List<IntegrationProfile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in config.Profiles)
        {
            var profile = Build(item);
            if (!names.Add(profile.Name))
                throw new InvalidOperationException($"Duplicate profile name '{profile.Name}'.");
            profiles.Add(profile);
        }
        return profiles;
    }

    public static IntegrationProfile Legacy(string name, string connectionString, string tag) => Build(new ConfigProfile
    {
        Name = name,
        Mode = name.StartsWith("oracle-", StringComparison.OrdinalIgnoreCase) ? "Oracle" : "MySql",
        ConnectionString = connectionString,
        Tags = new[] { tag },
    });

    private static IntegrationProfile Build(ConfigProfile item)
    {
        if (string.IsNullOrWhiteSpace(item.Name))
            throw new InvalidOperationException("Every profile needs a non-empty 'name'.");
        if (item.Mode is null || !Enum.TryParse<OceanBaseCompatibilityMode>(item.Mode, ignoreCase: false, out var mode)
            || !Enum.IsDefined(mode))
            throw new InvalidOperationException($"Profile '{item.Name}': 'mode' must be MySql or Oracle.");
        var resolved = ResolveConnectionString(item, requireValue: true);
        var options = item.Options ?? new ConfigOptions();
        var capabilities = item.Capabilities ?? new ConfigCapabilities();

        var builder = new OceanBaseConnectionStringBuilder(resolved);
        if (builder.ExpectedMode.HasValue && builder.ExpectedMode != mode)
            throw new InvalidOperationException($"Profile '{item.Name}': connection string ExpectedMode conflicts with the configured mode.");
        builder.ExpectedMode = mode;
        builder.UseOb20 = options.UseOb20 ?? builder.UseOb20;
        builder.EnableFullLinkTrace = options.EnableFullLinkTrace ?? builder.EnableFullLinkTrace;
        builder.UseOraclePrepareExecute = options.UseOraclePrepareExecute ?? builder.UseOraclePrepareExecute;
        builder.UseLobLocators = options.UseLobLocators ?? builder.UseLobLocators;
        builder.TreatTinyAsBoolean = options.TreatTinyAsBoolean ?? builder.TreatTinyAsBoolean;
        builder.UseAffectedRows = options.UseAffectedRows ?? builder.UseAffectedRows;
        if (options.LoadBalance is not null)
        {
            if (!Enum.TryParse<OceanBaseLoadBalance>(options.LoadBalance, ignoreCase: false, out var balance) || !Enum.IsDefined(balance))
                throw new InvalidOperationException($"Profile '{item.Name}': 'loadBalance' must be Sequential or RoundRobin.");
            builder.LoadBalance = balance;
        }
        if (options.TlsMode is not null)
        {
            if (!Enum.TryParse<OceanBaseTlsMode>(options.TlsMode, ignoreCase: false, out var tls) || !Enum.IsDefined(tls))
                throw new InvalidOperationException($"Profile '{item.Name}': 'tlsMode' must be VerifyFull, Required or Disabled.");
            // Profile options are an explicit override layer. Remove a raw
            // TLS/SslMode spelling before setting the canonical value so the
            // builder's user-input synonym conflict guard does not reject it.
            builder.Remove(OceanBaseConnectionStringBuilder.TlsModeKey);
            builder.TlsMode = tls;
        }
        if (builder.EnableFullLinkTrace && !builder.UseOb20)
            throw new InvalidOperationException($"Profile '{item.Name}': full-link trace requires 'useOb20'.");
        if (builder.UseLobLocators && !builder.UseOraclePrepareExecute)
            throw new InvalidOperationException($"Profile '{item.Name}': lob locators require 'useOraclePrepareExecute'.");

        return new IntegrationProfile
        {
            Name = item.Name,
            Mode = mode,
            ConnectionString = builder.ConnectionString,
            Tags = new HashSet<string>(item.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            CreateObjects = capabilities.CreateObjects,
            SlowQuery = capabilities.SlowQuery,
            AlternateDatabase = capabilities.AlternateDatabase,
            UnsupportedAuthConnectionString = ResolveOptionalEnvironment(capabilities.UnsupportedAuthEnvironment, item.Name, "unsupportedAuthEnvironment"),
            AutocommitDisabledConnectionString = ResolveOptionalEnvironment(capabilities.AutocommitDisabledEnvironment, item.Name, "autocommitDisabledEnvironment"),
        };
    }

    private static string ResolveConnectionString(ConfigProfile item, bool requireValue)
    {
        var hasText = !string.IsNullOrWhiteSpace(item.ConnectionString);
        var hasEnvironment = !string.IsNullOrWhiteSpace(item.ConnectionStringEnvironment);
        var hasSecret = !string.IsNullOrWhiteSpace(item.ConnectionStringSecret);
        if (Convert.ToInt32(hasText) + Convert.ToInt32(hasEnvironment) + Convert.ToInt32(hasSecret) != 1)
            throw new InvalidOperationException($"Profile '{item.Name}': set exactly one of 'connectionString', 'connectionStringEnvironment' or 'connectionStringSecret'.");
        var value = hasText ? item.ConnectionString : hasEnvironment
            ? Environment.GetEnvironmentVariable(item.ConnectionStringEnvironment!)
            : IntegrationEnvironment.GetSecret(item.ConnectionStringSecret!);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (requireValue)
                throw new InvalidOperationException($"Profile '{item.Name}': configured connection string secret/environment is empty.");
            return "";
        }
        return value!;
    }

    private static string? ResolveOptionalEnvironment(string? variable, string profile, string field)
    {
        if (string.IsNullOrWhiteSpace(variable)) return null;
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Profile '{profile}': '{field}' names variable '{variable}', which is empty. Remove the capability or provide the endpoint.");
        return value;
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidOperationException($"Duplicate configuration property '{property.Name}'.");
            RejectDuplicates(property.Value);
        }
    }

    private static JsonSerializerOptions Options => new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private sealed class ConfigFile
    {
        public ConfigProfile[] Profiles { get; set; } = Array.Empty<ConfigProfile>();
    }

    private sealed class ConfigProfile
    {
        public string Name { get; set; } = "";
        public string? Mode { get; set; }
        public string? ConnectionString { get; set; }
        public string? ConnectionStringEnvironment { get; set; }
        public string? ConnectionStringSecret { get; set; }
        public string[]? Tags { get; set; }
        public ConfigOptions? Options { get; set; }
        public ConfigCapabilities? Capabilities { get; set; }
    }

    private sealed class ConfigOptions
    {
        public bool? UseOb20 { get; set; }
        public bool? EnableFullLinkTrace { get; set; }
        public bool? UseOraclePrepareExecute { get; set; }
        public bool? UseLobLocators { get; set; }
        public bool? TreatTinyAsBoolean { get; set; }
        public bool? UseAffectedRows { get; set; }
        public string? LoadBalance { get; set; }
        public string? TlsMode { get; set; }
    }

    private sealed class ConfigCapabilities
    {
        public bool CreateObjects { get; set; }
        public string? SlowQuery { get; set; }
        public string? AlternateDatabase { get; set; }
        public string? UnsupportedAuthEnvironment { get; set; }
        public string? AutocommitDisabledEnvironment { get; set; }
    }
}
