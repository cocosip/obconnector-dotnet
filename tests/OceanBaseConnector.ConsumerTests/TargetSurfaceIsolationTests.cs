using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OceanBaseConnector;

namespace OceanBaseConnector.ConsumerTests
{
    /// <summary>
    /// Verifies the target-conditional API surface of the product assembly
    /// (A01: modern APIs must not leak into netstandard2.0). The netstandard2.0
    /// asset is inspected with a metadata reader instead of Assembly.Load so
    /// the check cannot be masked by assembly-name unification with the
    /// already-loaded modern asset.
    /// </summary>
    public sealed class TargetSurfaceIsolationTests
    {
        private static readonly string ModernTypeName = "OceanBaseConnector.OceanBaseModernApis";
        private static readonly string BaselineTypeName = "OceanBaseConnector.OceanBaseTargetSurface";

        private static IReadOnlySet<string> ReadPublicTypeNames(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var reader = new PEReader(stream);
            var metadata = reader.GetMetadataReader();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in metadata.TypeDefinitions)
            {
                var definition = metadata.GetTypeDefinition(handle);
                var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
                if (visibility != TypeAttributes.Public && visibility != TypeAttributes.NestedPublic)
                {
                    continue;
                }

                var name = metadata.GetString(definition.Name);
                var namespaceName = metadata.GetString(definition.Namespace);
                names.Add(string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name);
            }

            return names;
        }

        [Fact]
        public void BaselineConsumerUsesNetStandardAsset()
        {
            var context = new AssemblyLoadContext("baseline-sample", isCollectible: true);
            try
            {
                context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath("netstandard2.0"));
                var assembly = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,
                    "OceanBaseConnector.ConsumerTests.NetStandard.dll"));
                var sample = assembly.GetType("OceanBaseConnector.ConsumerTests.NetStandard.NetStandardConsumerSample")!;
                Assert.Contains("netstandard2.0", (string)sample.GetMethod("Describe")!.Invoke(null, null)!);
            }
            finally { context.Unload(); }
        }

        [Theory]
        [InlineData("netstandard2.0", ".NETStandard,Version=v2.0")]
        [InlineData("netstandard2.1", ".NETStandard,Version=v2.1")]
        public void NetStandardAssetRunsInIsolatedLoadContext(string target, string frameworkName)
        {
            var context = new AssemblyLoadContext("baseline-consumer", isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                Assert.Equal(frameworkName, assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName);
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnectionStringBuilder", throwOnError: true)!;
                var builder = Activator.CreateInstance(type, "Server=localhost;User ID=sample;Password=baseline-secret")!;
                var sanitized = (string)type.GetMethod("ToSanitizedConnectionString")!.Invoke(builder, new object[] { false })!;
                Assert.DoesNotContain("baseline-secret", sanitized);
                var property = assembly.GetType(BaselineTypeName)!.GetProperty("HasModernAdoNetApis")!;
                Assert.Equal(false, property.GetValue(null));
            }
            finally { context.Unload(); }
        }

        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        public void NetStandardAssetsDoNotExposeModernApiTypes(string target)
        {
            var path = RepositoryLocator.GetProductAssemblyPath(target);
            var typeNames = ReadPublicTypeNames(path);

            Assert.DoesNotContain(ModernTypeName, typeNames);
            Assert.DoesNotContain("OceanBaseConnector.OceanBaseDataSource", typeNames);
            Assert.DoesNotContain("OceanBaseConnector.OceanBaseDataSourceBuilder", typeNames);
            Assert.Contains(BaselineTypeName, typeNames);
        }

        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        public async Task NetStandardOpenCancellationClosesPendingHandshake(string target)
        {
            var context = new AssemblyLoadContext("cancel-" + target, isCollectible: true);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var opening = connection.OpenAsync(cancellation.Token);
                using var peer = await listener.AcceptTcpClientAsync(cancellation.Token);
                Assert.False(opening.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { context.Unload(); }
        }

        [Fact]
        public void NetStandard21OverridesPreparedAsyncEntrypoints()
        {
            var context = new AssemblyLoadContext("prepared-standard21", isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath("netstandard2.1"));
                var command = assembly.GetType("OceanBaseConnector.OceanBaseCommand")!;
                Assert.Equal(command, command.GetMethod("PrepareAsync", new[] { typeof(CancellationToken) })!.DeclaringType);
                Assert.Equal(command, command.GetMethod("DisposeAsync")!.DeclaringType);
            }
            finally { context.Unload(); }
        }

        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("net8.0")]
        [InlineData("net9.0")]
        [InlineData("net10.0")]
        public void ExactDecimalContractRunsInEachProductAsset(string target)
        {
            var context = new AssemblyLoadContext("decimal-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseDecimal", throwOnError: true)!;
                var number = Activator.CreateInstance(type, new object[] { new BigInteger(-123400), 4 })!;
                Assert.Equal(new BigInteger(-123400), type.GetProperty("Coefficient")!.GetValue(number));
                Assert.Equal(4, type.GetProperty("Scale")!.GetValue(number));
                Assert.Equal(-12.3400m, type.GetMethod("ToDecimal")!.Invoke(number, null));
                Assert.Equal("-12.3400", number.ToString());
                var parameter = (DbParameter)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseParameter")!)!;
                parameter.Value = number;
                Assert.Equal(DbType.Decimal, parameter.DbType);
                var providerType = parameter.GetType().GetProperty("OceanBaseDbType")!;
                var enumType = Nullable.GetUnderlyingType(providerType.PropertyType)!;
                providerType.SetValue(parameter, Enum.Parse(enumType, "UInt64"));
                Assert.Equal(DbType.UInt64, parameter.DbType);
                providerType.SetValue(parameter, Enum.Parse(enumType, "Binary"));
                Assert.Equal(DbType.Binary, parameter.DbType);
                providerType.SetValue(parameter, Enum.Parse(enumType, "Null"));
                Assert.Equal(DbType.Object, parameter.DbType);
                parameter.ResetDbType();
                Assert.Equal(DbType.Decimal, parameter.DbType);
                Assert.Equal(assembly, enumType.Assembly);
                providerType.SetValue(parameter, Enum.Parse(enumType, "Timestamp"));
                Assert.Equal(DbType.DateTime2, parameter.DbType);
                parameter.ResetDbType();
                Assert.Null(providerType.GetValue(parameter));
                Assert.Equal(DbType.Decimal, parameter.DbType);
            }
            finally { context.Unload(); }
        }

        [Fact]
        public void ModernAssetsExposeModernApiTypes()
        {
            foreach (var targetFramework in new[] { "net8.0", "net9.0", "net10.0" })
            {
                var path = RepositoryLocator.GetProductAssemblyPath(targetFramework);
                var typeNames = ReadPublicTypeNames(path);

                Assert.Contains(ModernTypeName, typeNames);
                Assert.Contains("OceanBaseConnector.OceanBaseDataSource", typeNames);
                Assert.Contains("OceanBaseConnector.OceanBaseDataSourceBuilder", typeNames);
                Assert.Contains(BaselineTypeName, typeNames);
            }
        }

        [Fact]
        public void AllAssetsCreateStandardProviderObjectsAndResolveLoggingDependencies()
        {
            foreach (var tfm in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
            {
                var context = new AssemblyLoadContext("provider-" + tfm, isCollectible: true);
                try
                {
                    var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(tfm));
                    var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                    Assert.False(factory.CanCreateDataAdapter);
                    Assert.False(factory.CanCreateCommandBuilder);
                    using var connection = factory.CreateConnection()!;
                    connection.ConnectionString = "Server=localhost;User ID=u;Password=baseline-secret";
                    Assert.Equal(ConnectionState.Closed, connection.State);
                    Assert.IsAssignableFrom<DbCommand>(connection.CreateCommand());
                    Assert.IsAssignableFrom<DbParameter>(factory.CreateParameter());
                    var loggerType = typeof(Microsoft.Extensions.Logging.ILoggerFactory);
                    Assert.NotNull(connection.GetType().GetConstructor(new[] { typeof(string), loggerType }));
                    Assert.DoesNotContain(assembly.GetReferencedAssemblies(), n => n.Name == "OceanBaseConnector.DependencyInjection");
                }
                finally { context.Unload(); }
            }
        }

        [Fact]
        public void RuntimeAssetMatchesModernTarget()
        {
            // The test host itself runs on a modern runtime and references the
            // matching product asset, so the compiled-in const must report true.
            Assert.True(OceanBaseTargetSurface.HasModernAdoNetApis);
        }
    }
}
