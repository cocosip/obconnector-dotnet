using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using OceanBaseConnector;
#if NET8_0_OR_GREATER
using System.IO;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
#endif

public static class PackageConsumer
{
    public static string Verify()
    {
#if NETSTANDARD2_0
        const string target = ".NETStandard,Version=v2.0";
        const int major = 8;
        const bool modern = false;
#elif NETSTANDARD2_1
        const string target = ".NETStandard,Version=v2.1";
        const int major = 8;
        const bool modern = false;
#else
#if NET8_0
        const int major = 8;
#elif NET9_0
        const int major = 9;
#else
        const int major = 10;
#endif
        const string target = ".NETCoreApp,Version=v" +
#if NET8_0
            "8.0";
#elif NET9_0
            "9.0";
#else
            "10.0";
#endif
        const bool modern = true;
#endif
        var assembly = typeof(OceanBaseConnection).Assembly;
        Check(assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName == target, "Product asset identity");
        var factory = OceanBaseProviderFactory.Instance;
        using var connection = factory.CreateConnection();
        using var command = factory.CreateCommand();
        var parameter = factory.CreateParameter();
        Check(connection.GetType() == typeof(OceanBaseConnection), "Connection type identity");
        Check(command.GetType() == typeof(OceanBaseCommand), "Command type identity");
        Check(parameter.GetType() == typeof(OceanBaseParameter), "Parameter type identity");
        var number = new OceanBaseDecimal(12.34m);
        parameter.Value = number;
        Check(parameter.DbType == DbType.Decimal && number.ToDecimal() == 12.34m, "Public decimal contract");
        foreach (var type in new[] { "OceanBaseDataSource", "OceanBaseDataSourceBuilder", "OceanBaseBatch", "OceanBaseBatchCommand" })
            Check((assembly.GetType("OceanBaseConnector." + type) != null) == modern, type + " visibility");
        Check(typeof(ILogger).Assembly.GetName().Version!.Major == major, "Logging assembly major");
        Check(typeof(ActivitySource).Assembly.GetName().Version!.Major == major, "DiagnosticSource assembly major");
        Check(connection.State == ConnectionState.Closed, "Factory must not connect");
        const string connectionString = "Server=localhost;User ID=package_sample;Password=package-secret;ExpectedMode=Oracle";
        connection.ConnectionString = connectionString;
        try
        {
            connection.OpenAsync(new CancellationToken(canceled: true)).GetAwaiter().GetResult();
            throw new InvalidOperationException("Pre-canceled open succeeded.");
        }
        catch (OperationCanceledException error)
        {
            Check(error.ToString().IndexOf("package-secret", StringComparison.Ordinal) < 0, "Cancellation error redaction");
            Check(connection.State == ConnectionState.Closed, "Pre-canceled open state");
        }
#if NET8_0_OR_GREATER
        var services = new ServiceCollection();
        services.AddOceanBaseDataSource(connectionString);
        services.AddKeyedOceanBaseDataSource("secondary", connectionString);
        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<OceanBaseDataSource>();
        Check(ReferenceEquals(source, provider.GetRequiredService<DbDataSource>()), "DI alias identity");
        Check(ReferenceEquals(source, provider.GetRequiredService<OceanBaseDataSource>()), "DI singleton");
        Check(!source.ConnectionString.Contains("package-secret", StringComparison.Ordinal), "Data source password redaction");
        var keyed = provider.GetRequiredKeyedService<OceanBaseDataSource>("secondary");
        Check(!ReferenceEquals(source, keyed) && ReferenceEquals(keyed, provider.GetRequiredKeyedService<DbDataSource>("secondary")), "Keyed DI identity");
        Check(typeof(OceanBaseServiceCollectionExtensions).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName == target, "DI target identity");
        using var ownedConnection = source.CreateConnection();
        Check(ownedConnection is OceanBaseConnection && ownedConnection.State == ConnectionState.Closed, "Data source factory");
        Check(factory.CanCreateBatch && connection.CanCreateBatch, "Batch support");
        using var batch = factory.CreateBatch();
        Check(batch is OceanBaseBatch && factory.CreateBatchCommand() is OceanBaseBatchCommand, "Batch type identity");
        using var factorySource = factory.CreateDataSource(connectionString);
        Check(factorySource is OceanBaseDataSource && !factorySource.ConnectionString.Contains("package-secret", StringComparison.Ordinal), "Factory data source redaction");
        provider.Dispose();
        foreach (var disposedSource in new[] { source, keyed })
        {
            try
            {
                using var unexpectedConnection = disposedSource.CreateConnection();
                throw new InvalidOperationException("DI did not dispose its data source.");
            }
            catch (ObjectDisposedException) { }
        }
#endif
        return target + " passed on " + Environment.Version;
    }

    private static void Check(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException("Package consumer failed: " + contract);
    }

#if NET8_0_OR_GREATER
    public static void Main(string[] arguments)
    {
        if (arguments.Length == 0) { Console.WriteLine(Verify()); return; }
        var path = Path.GetFullPath(arguments[0]);
        var context = new ConsumerContext(Path.GetDirectoryName(path)!);
        try
        {
            var consumer = context.LoadFromAssemblyPath(path).GetType(nameof(PackageConsumer))!;
            Console.WriteLine(consumer.GetMethod(nameof(Verify))!.Invoke(null, null));
        }
        finally { context.Unload(); }
    }

    private sealed class ConsumerContext : AssemblyLoadContext
    {
        private readonly string _directory;
        internal ConsumerContext(string directory) : base(isCollectible: true) => _directory = directory;
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(_directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
#endif
}
