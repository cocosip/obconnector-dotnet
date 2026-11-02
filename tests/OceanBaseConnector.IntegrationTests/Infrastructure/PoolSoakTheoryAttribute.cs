using System;
using System.Globalization;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

public sealed class PoolSoakTheoryAttribute : IntegrationTheoryAttribute
{
    internal const string DurationVariable = "OB_POOL_SOAK_SECONDS";

    public PoolSoakTheoryAttribute()
    {
        if (!IntegrationEnvironment.Requested) return;
        if (ParseDuration(Environment.GetEnvironmentVariable(DurationVariable)) == null)
            Skip = $"Pool soak not requested; set {DurationVariable}=3600 for the one-hour acceptance run.";
    }

    internal static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 1 || seconds > 86400)
            throw new ArgumentException($"{DurationVariable} must be an integer between 1 and 86400.");
        return TimeSpan.FromSeconds(seconds);
    }
}
