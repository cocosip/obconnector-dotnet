using System;
using System.Globalization;

namespace OceanBaseConnector.Internal
{
    internal sealed class DiscoveryBlacklistPolicy
    {
        internal DiscoveryBlacklistPolicy(bool retryDuration = false, int retryTimes = 1, int durationMilliseconds = 0, int timeoutSeconds = 50)
        {
            RetryDuration = retryDuration; RetryTimes = Math.Max(1, retryTimes);
            DurationMilliseconds = durationMilliseconds; TimeoutSeconds = timeoutSeconds;
            Key = (retryDuration ? "R:" : "N:") + RetryTimes.ToString(CultureInfo.InvariantCulture) + ":" +
                durationMilliseconds.ToString(CultureInfo.InvariantCulture) + ":" + timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        }
        internal bool RetryDuration { get; }
        internal int RetryTimes { get; }
        internal int DurationMilliseconds { get; }
        internal int TimeoutSeconds { get; }
        internal string Key { get; }
    }
}
