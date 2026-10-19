using System;
using System.Data.Common;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PamConfigurationTests
    {
        [Theory]
        [InlineData("password2")]
        [InlineData("PASSWORD123")]
        [InlineData("Password2147483647")]
        public void PamPasswordsRoundTripAndRemainSecret(string keyword)
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u");
            builder[keyword] = "private;token=secret";
            var full = new OceanBaseConnectionStringBuilder(builder.ToSanitizedConnectionString(true));
            Assert.Equal("private;token=secret", full[keyword]);
            Assert.Equal("private;token=secret", new OceanBaseConnectionStringBuilder(builder.ConnectionString)[keyword]);
            Assert.DoesNotContain("private", builder.ToSanitizedConnectionString(false));
            Assert.DoesNotContain("token", builder.Build().ToString());
            Assert.DoesNotContain("private", builder.Build().ForEndpoint(builder.Build().Endpoints[0]).ToString());
            Assert.True(builder.Remove(keyword));
            Assert.Null(builder[keyword]);
        }

        [Theory]
        [InlineData("Password0")]
        [InlineData("Password1")]
        [InlineData("Password02")]
        [InlineData("Password+2")]
        [InlineData("Password-2")]
        [InlineData("Password2x")]
        [InlineData("Password 2")]
        [InlineData("Password2147483648")]
        public void PamRejectsNonCanonicalIndicesWithoutEchoingValue(string keyword)
        {
            var builder = new OceanBaseConnectionStringBuilder();
            var error = Assert.Throws<ArgumentException>(() => builder[keyword] = "hidden-secret");
            Assert.DoesNotContain("hidden-secret", error.ToString());
        }

        [Fact]
        public void PamOptionsAndEndpointCopiesKeepFrozenPasswords()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=one,two;User ID=u;Password2=original;TLS Mode=VerifyFull");
            var options = builder.Build();
            builder["Password2"] = "changed";
            foreach (var endpoint in options.Endpoints)
            {
                var auth = new AuthenticationConversation(options.ForEndpoint(endpoint), "dialog", new byte[20]);
                Assert.Equal(new byte[] { 111, 114, 105, 103, 105, 110, 97, 108, 0 }, auth.Continue(new byte[] { 4 }));
            }
            var sanitized = new DbConnectionStringBuilder { ConnectionString = options.SanitizedConnectionString };
            Assert.False(sanitized.ContainsKey("Password2"));
        }
    }
}
