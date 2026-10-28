using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class CredentialProviderTests
    {

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SnapshotReplacesPamPasswordsAcrossEndpointCopyAndTls(bool asynchronous)
        {
            await using var server = new TestInfrastructure.CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, pam: true);
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { TlsMode = OceanBaseTlsMode.VerifyFull };
            configuration["Password2"] = "static-secondary";
            var provider = new Provider(new OceanBaseCredential("user", "fresh-primary", 1, additionalPasswords: new Dictionary<int, string> { [2] = "fresh-secondary" }));
            using var source = new OceanBaseDataSourceBuilder(configuration.ConnectionString).UseCredentialProvider(provider).UseRemoteCertificateValidationCallback((_, _, _, _) => true).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous);
            Assert.Equal(new[] { "fresh-primary\0", "fresh-secondary\0" }, System.Linq.Enumerable.Select(server.PamResponses, System.Text.Encoding.UTF8.GetString));
            await Close(connection, asynchronous);
            provider.Current = new OceanBaseCredential("user", "fresh-primary", 1, additionalPasswords: new Dictionary<int, string> { [2] = "changed-secondary" });
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(1, server.Accepted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExpiryBeforeAuthenticationContinuationSendsNoPamPassword(bool asynchronous)
        {
            await using var server = new TestInfrastructure.CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, pam: true);
            var expires = DateTimeOffset.UtcNow.AddSeconds(1); server.BeforeAuthSwitch = () => Expire(expires);
            var provider = new Provider(Credential(1, expires));
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port)) { TlsMode = OceanBaseTlsMode.VerifyFull }.ConnectionString;
            using var source = new OceanBaseDataSourceBuilder(configuration).UseCredentialProvider(provider).UseRemoteCertificateValidationCallback((_, _, _, _) => true).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous)); Assert.Empty(server.PamResponses);
        }
    }
}
