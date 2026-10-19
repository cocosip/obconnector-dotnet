using System;
using System.Data.Common;
using System.Linq;
using Microsoft.Extensions.Logging;
using OceanBaseConnector;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class OceanBaseServiceCollectionExtensions
    {
        /// <summary>Registers closed, transient connections owned by the resolving container scope.</summary>
        public static IServiceCollection AddOceanBaseConnection(this IServiceCollection services, string connectionString)
        {
            Validate(services, connectionString);
            var configuration = new OceanBaseConnectionStringBuilder(connectionString).ConnectionString;
            if (Has(services, typeof(OceanBaseConnection), null))
                throw new InvalidOperationException("An OceanBase connection is already registered.");
            var addAlias = !Has(services, typeof(DbConnection), null);
            services.AddTransient(sp => new OceanBaseConnection(configuration, sp.GetService<ILoggerFactory>()));
            if (addAlias) services.AddTransient<DbConnection>(sp => sp.GetRequiredService<OceanBaseConnection>());
            return services;
        }

#if NET8_0_OR_GREATER
        public static IServiceCollection AddOceanBaseDataSource(this IServiceCollection services, string connectionString,
            Action<IServiceProvider, OceanBaseDataSourceBuilder>? configure = null)
        {
            Validate(services, connectionString);
            if (Has(services, typeof(OceanBaseDataSource), null)) throw Conflict();
            var addAlias = !Has(services, typeof(DbDataSource), null);
            services.AddSingleton(sp => Build(sp, connectionString, configure));
            if (addAlias) services.AddSingleton<DbDataSource>(sp => sp.GetRequiredService<OceanBaseDataSource>());
            return services;
        }

        public static IServiceCollection AddKeyedOceanBaseDataSource(this IServiceCollection services, object serviceKey, string connectionString,
            Action<IServiceProvider, OceanBaseDataSourceBuilder>? configure = null)
        {
            Validate(services, connectionString);
            if (serviceKey == null) throw new ArgumentNullException(nameof(serviceKey));
            if (Equals(serviceKey, KeyedService.AnyKey)) throw new ArgumentException("A concrete service key is required.", nameof(serviceKey));
            if (Has(services, typeof(OceanBaseDataSource), serviceKey) || Has(services, typeof(DbDataSource), serviceKey)) throw Conflict();
            services.AddKeyedSingleton<OceanBaseDataSource>(serviceKey, (sp, _) => Build(sp, connectionString, configure));
            services.AddKeyedSingleton<DbDataSource>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<OceanBaseDataSource>(key));
            return services;
        }

        private static OceanBaseDataSource Build(IServiceProvider services, string connectionString,
            Action<IServiceProvider, OceanBaseDataSourceBuilder>? configure)
        {
            var builder = new OceanBaseDataSourceBuilder(connectionString).UseLoggerFactory(services.GetService<ILoggerFactory>());
            configure?.Invoke(services, builder);
            return builder.Build();
        }
#endif

        private static bool Has(IServiceCollection services, Type type, object? key) => services.Any(d =>
            d.ServiceType == type && (key == null ? !d.IsKeyedService : d.IsKeyedService && Equals(d.ServiceKey, key)));
        private static InvalidOperationException Conflict() => new InvalidOperationException("A data source is already registered for this service key.");
        private static void Validate(IServiceCollection services, string connectionString)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));
        }
    }
}
