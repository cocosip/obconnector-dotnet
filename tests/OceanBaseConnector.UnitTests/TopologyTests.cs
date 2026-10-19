using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class TopologyTests
    {
        [Theory]
        [InlineData("Sequential", OceanBaseLoadBalance.Sequential)]
        [InlineData("roundrobin", OceanBaseLoadBalance.RoundRobin)]
        public void TopologyLoadBalanceIsStrictAndFrozen(string value, OceanBaseLoadBalance expected)
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1,ob2;User ID=u;Load Balance=" + value);
            var options = builder.Build();
            Assert.Equal(expected, builder.LoadBalance); Assert.Equal(expected, options.LoadBalance);
            Assert.Equal(expected, options.ForEndpoint(options.Endpoints[1]).LoadBalance);
            Assert.Equal(expected, new OceanBaseConnectionStringBuilder(builder.ToSanitizedConnectionString(false)).LoadBalance);
            builder.LoadBalance = OceanBaseLoadBalance.Sequential;
            Assert.Equal(expected, options.LoadBalance);
            builder.Remove("Load Balance"); Assert.Equal(OceanBaseLoadBalance.Sequential, builder.LoadBalance);
            Assert.Equal(OceanBaseLoadBalance.Sequential, new OceanBaseConnectionStringBuilder().LoadBalance);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("-1")]
        [InlineData("Random")]
        [InlineData("RoundRobin,Sequential")]
        public void TopologyRejectsUnknownStrategies(string value)
        {
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder("Load Balance=" + value));
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder { LoadBalance = (OceanBaseLoadBalance)42 });
        }

        [Fact]
        public void TopologySelectorRotatesAndSurvivesCounterOverflow()
        {
            var selector = new EndpointSelector();
            Assert.Equal(new[] { 0, 1, 2, 0, 1, 2 }, Enumerable.Range(0, 6).Select(_ => selector.SelectStart(3, OceanBaseLoadBalance.RoundRobin)));
            typeof(EndpointSelector).GetField("_next", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(selector, int.MaxValue - 1);
            Assert.Equal((int)((uint)int.MaxValue % 3), selector.SelectStart(3, OceanBaseLoadBalance.RoundRobin));
            Assert.Equal((int)(0x80000000U % 3), selector.SelectStart(3, OceanBaseLoadBalance.RoundRobin));
            for (var i = 0; i < 3; i++) Assert.Equal(0, selector.SelectStart(3, OceanBaseLoadBalance.Sequential));
        }

        [Fact]
        public void TopologySharedSelectorIsFairUnderConcurrentOpens()
        {
            var selector = new EndpointSelector();
            var counts = new int[3];
            Parallel.For(0, 3000, _ => System.Threading.Interlocked.Increment(ref counts[selector.SelectStart(3, OceanBaseLoadBalance.RoundRobin)]));
            Assert.Equal(new[] { 1000, 1000, 1000 }, counts);
        }
    }
}
