using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OceanBaseConnector.Acceptance;

internal sealed class AcceptanceConfig
{
    public string Mode { get; set; } = "";
    public string? ConnectionString { get; set; }
    public string? ConnectionStringEnvironment { get; set; }
    public string[] Groups { get; set; } = Array.Empty<string>();
    public bool AllowCreateObjects { get; set; }
    public bool ZeroDateProfile { get; set; }
    public bool NetworkFaultProfile { get; set; }
    public bool XaCrashProfile { get; set; }
    public bool TopologyFaultProfile { get; set; }
    public int LargeValueBytes { get; set; } = 65536;
    public string? SlowQuery { get; set; }
    public string? AlternateDatabase { get; set; }

    public static AcceptanceConfig Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new ArgumentException("Duplicate configuration property.");
        var config = JsonSerializer.Deserialize<AcceptanceConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        }) ?? throw new ArgumentException("Configuration must be an object.");
        if (config.Mode != "MySql" && config.Mode != "Oracle") throw new ArgumentException("Mode must be MySql or Oracle.");
        if (string.IsNullOrWhiteSpace(config.ConnectionString) == string.IsNullOrWhiteSpace(config.ConnectionStringEnvironment))
            throw new ArgumentException("Set exactly one of connectionString or connectionStringEnvironment.");
        if (config.Groups == null || config.Groups.Any(g => g == null || !DatabaseCases.Groups.Contains(g, StringComparer.Ordinal)))
            throw new ArgumentException("Unknown test group; use --list for available groups.");
        if (config.LargeValueBytes < 1 || config.LargeValueBytes > 1048576)
            throw new ArgumentException("largeValueBytes must be between 1 and 1048576.");
        return config;
    }

    public string ResolveConnectionString(Func<string, string?> environment)
    {
        var value = !string.IsNullOrWhiteSpace(ConnectionString) ? ConnectionString : environment(ConnectionStringEnvironment!);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Connection string environment variable is empty.");
        var builder = new OceanBaseConnectionStringBuilder(value);
        var mode = Mode == "Oracle" ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql;
        if (builder.ExpectedMode.HasValue && builder.ExpectedMode != mode)
            throw new ArgumentException("Configured mode conflicts with connection string ExpectedMode.");
        builder.ExpectedMode = mode;
        if (builder.ConnectionTimeout <= 0 || builder.CommandTimeout <= 0)
            throw new ArgumentException("Acceptance requires finite positive connection and command timeouts.");
        // Build validates the immutable product options without opening a socket.
        using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        return builder.ConnectionString;
    }
}
