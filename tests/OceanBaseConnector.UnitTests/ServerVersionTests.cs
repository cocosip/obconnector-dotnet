using System;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ServerVersionTests
    {
        [Theory]
        [InlineData("2.2.5", "2.2.5.0")]
        [InlineData("2.2.76.0", "2.2.76.0")]
        [InlineData("4.4.2.1", "4.4.2.1")]
        [InlineData("4.4.2.2-private", "4.4.2.2")]
        [InlineData("4.3.5.0 (r123-build)", "4.3.5.0")]
        [InlineData("OceanBase_CE v4.3.5.1", "4.3.5.1")]
        [InlineData("5.7.25-OceanBase 2.2.75", "2.2.75.0")]
        [InlineData("5.7.25-OceanBase-v4.4.2.2", "4.4.2.2")]
        [InlineData("9.0.0+build.1", "9.0.0.0")]
        public void ParsesProductVersionWithoutUsingCompatibilityPrefix(string text, string expected)
            => Assert.Equal(Version.Parse(expected), OceanBaseServerVersion.Parse(text));

        [Theory]
        [InlineData(null)] [InlineData("")] [InlineData("unknown-secret")]
        [InlineData("4.4")] [InlineData("4.4..2")] [InlineData("4.4.2.2.1")]
        [InlineData("4.4.2.2\n")] [InlineData("4.4.2.2-build\n")]
        [InlineData("0.1.2")] [InlineData("2147483648.1.2")]
        [InlineData("5.7.25-OceanBase unknown")]
        [InlineData("5.7.25-OceanBase 4.bad")]
        [InlineData("garbage\nOceanBase 4.4.2.2")]
        [InlineData("unknown OceanBase 4.4.2.2")]
        [InlineData("5.7-OceanBase 4.4.2.2")]
        [InlineData("OceanBase 4.4.2.2-OceanBase 9.0.0")]
        public void MalformedOrAmbiguousVersionCannotEnableFeatures(string? text)
            => Assert.Null(OceanBaseServerVersion.Parse(text));

        [Fact]
        public void CompatibilityPrefixDoesNotEnablePrepareExecuteForAnOldProduct()
            => Assert.True(OceanBaseServerVersion.Parse("5.7.25-OceanBase 2.2.75") < new Version(2, 2, 76));
    }

    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task PublicVersionAndSchemaUseIdentityRatherThanGreeting(bool asynchronous)
        {
            const string actual = "4.3.5.1-build";
            var server = Server(_ => Task.CompletedTask, serverVersion: actual);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(actual, connection.ServerVersion);
                var schema = asynchronous ? await connection.GetSchemaAsync("DataSourceInformation") : connection.GetSchema("DataSourceInformation");
                Assert.Equal(actual, schema.Rows[0]["DataSourceProductVersion"]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
