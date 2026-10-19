using System;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class GssapiConfigurationTests
    {
        [Fact]
        public void GssapiPrincipalRoundTripsAndSurvivesFrozenEndpointCopies()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=one,two;User ID=u;Service Principal Name=mariadb/trusted@REALM");
            var options = builder.Build();
            builder["Service Principal Name"] = "changed";
            foreach (var endpoint in options.Endpoints)
                Assert.Equal("mariadb/trusted@REALM", options.ForEndpoint(endpoint).ServicePrincipalName);
            Assert.Equal("mariadb/trusted@REALM", new OceanBaseConnectionStringBuilder(options.SanitizedConnectionString)["Service Principal Name"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GssapiRejectsInvalidConfiguredPrincipalsWithoutEcho(bool unicode)
        {
            var builder = new OceanBaseConnectionStringBuilder();
            var value = unicode ? "hidden\ud800secret" : "hidden\nsecret";
            var error = Assert.Throws<ArgumentException>(() => builder["Service Principal Name"] = value);
            Assert.DoesNotContain("hidden", error.ToString());
            Assert.DoesNotContain("secret", error.ToString());
        }
    }
}
