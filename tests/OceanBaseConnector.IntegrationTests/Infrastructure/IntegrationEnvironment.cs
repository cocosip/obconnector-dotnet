using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

/// <summary>
/// Entry point for the opt-in integration environment. Nothing connects to a
/// database unless <see cref="RequireIntegrationVariable"/> is set to "1": the
/// fact and theory attributes then skip with a visible reason, and every
/// profile data source enumerates zero cases. With the variable set, profile
/// resolution must succeed or the run fails loudly — a gate run must never
/// pass on a half-configured environment.
/// </summary>
public static class IntegrationEnvironment
{
    public const string RequireIntegrationVariable = "OB_REQUIRE_INTEGRATION";
    public const string ConfigPathVariable = "OB_INTEGRATION_CONFIG";
    public const string ProfileSelectionVariable = "OB_INTEGRATION_PROFILES";

    // Legacy gate contract (docs/obconnector-dotnet-progress.md): without a
    // config file the four documented OB_*_CONNECTION_STRING variables
    // synthesize the built-in profiles.
    internal static readonly (string Name, string Variable, string Tag)[] LegacyVariables =
    {
        ("mysql-direct", "OB_MYSQL_CONNECTION_STRING", "direct"),
        ("oracle-direct", "OB_ORACLE_CONNECTION_STRING", "direct"),
        ("mysql-proxy", "OB_MYSQL_PROXY_CONNECTION_STRING", "proxy"),
        ("oracle-proxy", "OB_ORACLE_PROXY_CONNECTION_STRING", "proxy"),
    };

    private static readonly object Gate = new();
    private static IReadOnlyList<IntegrationProfile>? _profiles;
    private static Exception? _failure;

    public static bool Requested =>
        Environment.GetEnvironmentVariable(RequireIntegrationVariable) == "1";

    /// <summary>
    /// All configured profiles, filtered by the optional selection variable.
    /// Throws when integration was requested but the configuration is broken;
    /// returns an empty list when integration was not requested.
    /// </summary>
    public static IReadOnlyList<IntegrationProfile> Profiles
    {
        get
        {
            lock (Gate)
            {
                if (_profiles is null || _failure is not null)
                {
                    try { _profiles = Load(); _failure = null; }
                    catch (Exception error) { _failure = error; throw; }
                }
                return _profiles;
            }
        }
    }

    public static IntegrationProfile GetProfile(string name)
    {
        var profile = Profiles.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            throw new InvalidOperationException($"Profile '{name}' is not configured; it disappeared during this run.");
        return profile;
    }

    private static IReadOnlyList<IntegrationProfile> Load()
    {
        if (!Requested) return Array.Empty<IntegrationProfile>();
        var profiles = LoadConfigFile() ?? LoadLegacyEnvironment();
        var selected = Environment.GetEnvironmentVariable(ProfileSelectionVariable);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            var wanted = selected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            var missing = wanted.Where(name => !profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"{ProfileSelectionVariable} references unknown profile(s): {string.Join(", ", missing)}. Configured: {string.Join(", ", profiles.Select(p => p.Name))}.");
            profiles = profiles.Where(p => wanted.Any(name => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        return profiles;
    }

    private static IReadOnlyList<IntegrationProfile>? LoadConfigFile()
    {
        var path = Environment.GetEnvironmentVariable(ConfigPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            var root = FindRepositoryRoot();
            if (root is null) return null;
            path = Path.Combine(root, "tests", "OceanBaseConnector.IntegrationTests", "integration-tests.json");
            if (!File.Exists(path)) return null;
        }
        // dotnet test hosts run from their own directories; also resolve
        // caller-relative paths against the repository root.
        if (!File.Exists(path) && !Path.IsPathRooted(path))
        {
            var root = FindRepositoryRoot();
            if (root is not null)
            {
                var candidate = Path.GetFullPath(Path.Combine(root, path));
                if (File.Exists(candidate)) path = candidate;
            }
        }
        if (!File.Exists(path))
            throw new InvalidOperationException($"{ConfigPathVariable} points at '{path}', which does not exist.");
        return IntegrationProfileConfig.Parse(File.ReadAllText(path));
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OceanBaseConnector.slnx")))
                return directory.FullName;
        }
        return null;
    }

    private static IReadOnlyList<IntegrationProfile> LoadLegacyEnvironment()
    {
        var profiles = new List<IntegrationProfile>();
        var missing = new List<string>();
        foreach (var (name, variable, tag) in LegacyVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value)) { missing.Add(variable); continue; }
            profiles.Add(IntegrationProfileConfig.Legacy(name, value, tag));
        }
        if (profiles.Count == 0)
            throw new InvalidOperationException(
                $"Integration was requested but no configuration is available. Set {ConfigPathVariable} to a profiles file, or set the legacy gate variables: {string.Join(", ", missing)}.");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Legacy gate mode requires all four connection strings; missing: {string.Join(", ", missing)}.");
        return profiles;
    }
}
