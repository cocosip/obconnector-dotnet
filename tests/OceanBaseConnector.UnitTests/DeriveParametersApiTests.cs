using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class DeriveParametersApiTests
    {
        [Fact]
        public void CommandExposesSynchronousDerivationWithAnOptionalOverloadSelector()
        {
            var method = typeof(OceanBaseCommand).GetMethod("DeriveParameters", new[] { typeof(string) });
            Assert.NotNull(method);
            Assert.Equal(typeof(void), method!.ReturnType);
            Assert.True(method.GetParameters()[0].IsOptional);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EveryAssetCanUseTheAsyncApiWithCancellation(bool overload)
        {
            var signature = overload ? new[] { typeof(string), typeof(CancellationToken) } : new[] { typeof(CancellationToken) };
            var method = typeof(OceanBaseCommand).GetMethod("DeriveParametersAsync", signature);
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }
    }
}
