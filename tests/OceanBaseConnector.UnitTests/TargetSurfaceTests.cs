using System;
using OceanBaseConnector;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class TargetSurfaceTests
    {
        [Fact]
        public void ModernRuntimeSeesModernApiSurface()
        {
            // Test hosts run on net8.0/net9.0/net10.0 and consume the
            // matching product asset, so the flag must follow the runtime.
            var isModernRuntime = Environment.Version.Major >= 8;
            Assert.Equal(isModernRuntime, OceanBaseTargetSurface.HasModernAdoNetApis);
        }
    }
}
