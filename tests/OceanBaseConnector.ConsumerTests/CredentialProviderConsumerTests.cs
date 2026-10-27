using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    [Collection("Consumer diagnostics")]
    public sealed class CredentialProviderConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var oracle in new[] { false, true })
                        foreach (var source in new[] { false, true })
                            if (!source || target.StartsWith("netstandard", StringComparison.Ordinal) == false)
                                yield return new object[] { target, asynchronous, oracle, source };
        }
        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetAuthenticatesReusesRotatesAndFailsClosed(string target, bool asynchronous, bool oracle, bool sourceOwned)
        {
            using var fixture = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = fixture.RootElement.GetProperty("payloadBase64").GetBytesFromBase64();
            await using var server = new CredentialServer(greeting, oracle);
            var context = new AssemblyLoadContext("credential-" + target, isCollectible: true);
            DbConnection? connection = null; DbDataSource? source = null; MethodInfo? clear = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var helpers = context.LoadFromAssemblyPath(typeof(NetStandard.RotatingCredentialProvider).Assembly.Location);
                var providerType = helpers.GetType("OceanBaseConnector.ConsumerTests.NetStandard.RotatingCredentialProvider")!;
                var provider = Activator.CreateInstance(providerType, new object[] { asynchronous })!;
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!; clear = type.GetMethod("ClearPool")!;
                var configuration = new DbConnectionStringBuilder { ["Server"] = "127.0.0.1", ["Port"] = server.Port, ["User ID"] = "configured-user", ["Password"] = "static-password", ["TLS Mode"] = "Disabled", ["Pooling"] = true, ["Connection Timeout"] = 5 }.ConnectionString;
                if (sourceOwned)
                {
                    var builderType = assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!;
                    var builder = Activator.CreateInstance(builderType, configuration)!;
                    builderType.GetMethod("UseCredentialProvider")!.Invoke(builder, new[] { provider });
                    source = (DbDataSource)builderType.GetMethod("Build")!.Invoke(builder, null)!; connection = source.CreateConnection();
                }
                else
                {
                    connection = (DbConnection)Activator.CreateInstance(type, configuration)!;
                    type.GetProperty("CredentialProvider")!.SetValue(connection, provider);
                }
                await Open(connection, asynchronous); await Close(connection, asynchronous);
                await Open(connection, asynchronous); await Close(connection, asynchronous); Assert.Equal(1, server.Accepted);
                providerType.GetProperty("Version")!.SetValue(provider, 2L);
                await Open(connection, asynchronous); await Close(connection, asynchronous); Assert.Equal(2, server.Accepted);
                Assert.Equal(new[] { "consumer-user", "consumer-user" }, server.Authentications.Select(auth => auth.User));
                var first = SHA1.HashData(Encoding.UTF8.GetBytes("consumer-secret-2"));
                var last = SHA1.HashData(Convert.FromHexString("01020304050607081112131415161718191a1b1c").Concat(SHA1.HashData(first)).ToArray());
                Assert.Equal(first.Zip(last, (a, b) => (byte)(a ^ b)), server.Authentications.Last().Response);
                providerType.GetProperty("Fail")!.SetValue(provider, true);
                var error = await Record.ExceptionAsync(() => Open(connection, asynchronous)); Assert.NotNull(error);
                Assert.DoesNotContain("consumer-provider-secret", error.ToString()); Assert.Equal(2, server.Accepted);
                providerType.GetProperty("Fail")!.SetValue(provider, false);
                await Open(connection, asynchronous); await Close(connection, asynchronous);
                Assert.Equal(2, server.Accepted); Assert.Equal(5, providerType.GetProperty("Calls")!.GetValue(provider));
                clear.Invoke(null, new object[] { connection });
            }
            finally
            {
                if (connection != null) { connection.Close(); if (source == null) clear?.Invoke(null, new object[] { connection }); connection.Dispose(); }
                source?.Dispose(); context.Unload();
            }
        }
        private static Task Open(DbConnection connection, bool asynchronous) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
        private static Task Close(DbConnection connection, bool asynchronous) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
    }
}
