using System;
using OceanBaseConnector;

namespace OceanBaseConnector.ConsumerTests.NetStandard
{
    /// <summary>
    /// Compile-time consumption sample for the netstandard2.0 asset. If a
    /// modern-only API ever leaks into the baseline asset, this file is where
    /// accidental use would compile silently; the MetadataReader assertions in
    /// the runnable consumer tests are the enforcement, this file is the smoke
    /// sample. It must only reference APIs available on netstandard2.0.
    /// </summary>
    public static class NetStandardConsumerSample
    {
        public static string Describe()
        {
            if (OceanBaseTargetSurface.HasModernAdoNetApis)
            {
                throw new InvalidOperationException(
                    "netstandard2.0 asset must not report modern ADO.NET APIs.");
            }

            return "netstandard2.0 consumer compiles against the baseline asset.";
        }
    }
}
