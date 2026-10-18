using System;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.DependencyInjection.Tests
{
    public sealed class RegistrationTests
    {
        private const string Configuration = "Server=unreachable.invalid;User ID=u;Password=secret";

        [Fact]
        public void LazyFactoryUsesHostLoggerAndContainerOwnsSingleton()
        {
            var services = new ServiceCollection();
            var logger = new RecordingLoggerFactory();
            services.AddSingleton<ILoggerFactory>(logger);
            var calls = 0;
            services.AddOceanBaseDataSource(Configuration, (sp, builder) =>
            {
                Assert.Same(logger, sp.GetRequiredService<ILoggerFactory>());
                calls++;
            });
            Assert.Equal(0, calls);
            var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var source = container.GetRequiredService<OceanBaseDataSource>();
            Assert.Equal(1, calls);
            Assert.Same(source, container.GetRequiredService<DbDataSource>());
            using (var scope = container.CreateScope()) Assert.Same(source, scope.ServiceProvider.GetRequiredService<OceanBaseDataSource>());
            Assert.Null(container.GetService<DbConnection>());
            Assert.Null(container.GetService<OceanBaseConnection>());
            container.Dispose();
            Assert.Throws<ObjectDisposedException>(() => source.CreateConnection());
            Assert.Equal(0, logger.DisposeCount);
        }

        [Fact]
        public async System.Threading.Tasks.Task KeyedSourcesAreIndependentAndAsyncDisposed()
        {
            var services = new ServiceCollection();
            services.AddKeyedOceanBaseDataSource("orders", Configuration);
            services.AddKeyedOceanBaseDataSource("reporting", Configuration, (_, b) => b.ConnectionStringBuilder.Server = "reporting.invalid");
            var container = services.BuildServiceProvider();
            var first = container.GetRequiredKeyedService<OceanBaseDataSource>("orders");
            var second = container.GetRequiredKeyedService<OceanBaseDataSource>("reporting");
            Assert.NotSame(first, second);
            Assert.Same(first, container.GetRequiredKeyedService<DbDataSource>("orders"));
            Assert.Same(second, container.GetRequiredKeyedService<DbDataSource>("reporting"));
            using var connection = second.CreateConnection();
            Assert.Equal("reporting.invalid", connection.DataSource);
            Assert.Null(container.GetService<OceanBaseDataSource>());
            await container.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => first.CreateConnection());
            Assert.Throws<ObjectDisposedException>(() => second.CreateConnection());
        }

        [Fact]
        public void RegistrationConflictsFailWithoutPartialServices()
        {
            var services = new ServiceCollection();
            services.AddOceanBaseDataSource(Configuration);
            var count = services.Count;
            Assert.Throws<InvalidOperationException>(() => services.AddOceanBaseDataSource(Configuration));
            Assert.Equal(count, services.Count);
            services.AddKeyedOceanBaseDataSource("key", Configuration);
            count = services.Count;
            Assert.Throws<InvalidOperationException>(() => services.AddKeyedOceanBaseDataSource("key", Configuration));
            Assert.Equal(count, services.Count);
            var other = new ServiceCollection();
            other.AddKeyedSingleton<DbDataSource>("key", (_, _) => throw new Exception("must not resolve"));
            Assert.Throws<InvalidOperationException>(() => other.AddKeyedOceanBaseDataSource("key", Configuration));
            Assert.Single(other);
        }

        [Fact]
        public void ExistingGenericRegistrationIsPreserved()
        {
            var services = new ServiceCollection();
            using var existing = new OceanBaseDataSourceBuilder("Server=existing;User ID=u").Build();
            services.AddSingleton<DbDataSource>(existing);
            services.AddOceanBaseDataSource(Configuration);
            using var provider = services.BuildServiceProvider();
            Assert.Same(existing, provider.GetRequiredService<DbDataSource>());
            Assert.NotSame(existing, provider.GetRequiredService<OceanBaseDataSource>());
        }

        [Fact]
        public void InvalidArgumentsAreRejectedWithoutRegistration()
        {
            var services = new ServiceCollection();
            Assert.Throws<ArgumentException>(() => services.AddOceanBaseDataSource(" "));
            Assert.Throws<ArgumentNullException>(() => services.AddKeyedOceanBaseDataSource(null!, Configuration));
            Assert.Empty(services);
        }

        [Fact]
        public async System.Threading.Tasks.Task CallbackCanOverrideHostLoggerFactory()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            var services = new ServiceCollection();
            var hostLog = new RecordingLoggerFactory();
            var overrideLog = new RecordingLoggerFactory();
            services.AddSingleton<ILoggerFactory>(hostLog);
            services.AddOceanBaseDataSource($"Server=127.0.0.1;Port={port};User ID=u;TLS Mode=Disabled;Connection Timeout=5", (_, builder) => builder.UseLoggerFactory(overrideLog));
            using var provider = services.BuildServiceProvider();
            var source = provider.GetRequiredService<OceanBaseDataSource>();
            Assert.Equal(0, hostLog.CreateCount);
            Assert.Equal(3, overrideLog.CreateCount);
            try
            {
                var opening = source.OpenConnectionAsync().AsTask();
                using (var client = await accepted.WaitAsync(TimeSpan.FromSeconds(10))) { }
                await Assert.ThrowsAsync<OceanBaseException>(() => opening);
                Assert.Empty(hostLog.Entries);
                Assert.Contains(overrideLog.Entries, e => e.EventId.Id == 1002);
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public void SingletonConfigurationCannotResolveScopedServices()
        {
            var services = new ServiceCollection();
            services.AddScoped<object>();
            services.AddOceanBaseDataSource(Configuration, (sp, _) => sp.GetRequiredService<object>());
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<OceanBaseDataSource>());
        }
    }
}
