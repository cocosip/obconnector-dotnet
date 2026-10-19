using System;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.DependencyInjection.Tests
{
    public sealed class ConnectionRegistrationTests
    {
        private const string Configuration = "Server=unreachable.invalid;User ID=u;Password=secret;Password2=second-secret;Service Principal Name=mariadb/host@REALM";

        [Fact]
        public void EachResolutionCreatesAClosedConnectionAndTheScopeDisposesIt()
        {
            var services = new ServiceCollection();
            var log = new RecordingLoggerFactory();
            services.AddSingleton<ILoggerFactory>(log);
            Assert.Same(services, Register(services, Configuration));
            Assert.Equal(0, log.CreateCount);
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var scope = provider.CreateScope();
            var first = scope.ServiceProvider.GetRequiredService<OceanBaseConnection>();
            var second = scope.ServiceProvider.GetRequiredService<OceanBaseConnection>();
            var generic = Assert.IsType<OceanBaseConnection>(scope.ServiceProvider.GetRequiredService<DbConnection>());
            Assert.NotSame(first, second);
            Assert.NotSame(first, generic);
            Assert.Equal(ConnectionState.Closed, first.State);
            Assert.Equal("unreachable.invalid", first.DataSource);
            Assert.Equal("mariadb/host@REALM", new OceanBaseConnectionStringBuilder(first.ConnectionString).ServicePrincipalName);
            Assert.True(log.CreateCount > 0);
            first.ConnectionString = "Server=changed;User ID=u";
            Assert.Equal("unreachable.invalid", second.DataSource);
            scope.Dispose();
            foreach (var connection in new[] { first, second, generic })
                Assert.Throws<ObjectDisposedException>(() => connection.ConnectionString = Configuration);
            Assert.Equal(0, log.DisposeCount);
        }

        [Fact]
        public void ExistingGenericProviderIsPreservedAndTypedDuplicatesAreAtomic()
        {
            var services = new ServiceCollection();
            using var existing = new OceanBaseConnection("Server=existing;User ID=u");
            services.AddSingleton<DbConnection>(existing);
            Register(services, Configuration);
            var count = services.Count;
            Assert.Throws<InvalidOperationException>(() => Register(services, Configuration));
            Assert.Equal(count, services.Count);
            using var provider = services.BuildServiceProvider();
            Assert.Same(existing, provider.GetRequiredService<DbConnection>());
            using var connection = provider.GetRequiredService<OceanBaseConnection>();
            Assert.NotSame(existing, connection);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("Unknown=hidden-secret")]
        public void InvalidConfigurationDoesNotPartiallyRegister(string configuration)
        {
            var services = new ServiceCollection();
            Assert.ThrowsAny<ArgumentException>(() => Register(services, configuration));
            Assert.Empty(services);
        }

        [Fact]
        public void NullServicesAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => Register(null!, Configuration));
        }

        private static IServiceCollection Register(IServiceCollection services, string configuration)
        {
            return services.AddOceanBaseConnection(configuration);
        }
    }
}
