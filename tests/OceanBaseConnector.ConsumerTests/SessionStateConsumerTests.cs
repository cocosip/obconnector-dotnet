using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class SessionStateConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var oracle in new[] { false, true })
                        yield return new object[] { target, asynchronous, oracle };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetRetiresStatefulCallsButReusesOrdinaryAndRejectedSql(string target, bool asynchronous, bool oracle)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            await using var server = new CredentialServer(fixture.RootElement.GetProperty("payloadBase64").GetBytesFromBase64(), oracle,
                sessionStateOk: oracle ? SessionTimeZoneFixture.Report : null);
            var context = new AssemblyLoadContext("session-state-" + target, isCollectible: true);
            DbConnection? connection = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={server.Port};User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1";
                await Open();
                await Execute("SELECT 'SET x = 1; CALL p()'");
                await Close(); await Open(); Assert.Equal(1, server.Accepted);
                await Execute(oracle ? "/* trace */ ALTER\nSESSION SET TIME_ZONE = '+00:00'" : "# trace\nSET SESSION time_zone = '+00:00'");
                await Close(); await Open(); Assert.Equal(2, server.Accepted);
                await Execute("CALL p()");
                await Close(); await Open(); Assert.Equal(3, server.Accepted);
                await Assert.ThrowsAsync<NotSupportedException>(() => Execute("SET a = 1; SET b = 2"));
                await Close(); await Open(); Assert.Equal(3, server.Accepted);
                await Execute("SELECT 1"); await Close();

                Task Open() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
                Task Close() => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
                async Task Execute(string sql)
                {
                    using var command = connection.CreateCommand(); command.CommandText = sql;
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
            }
            finally
            {
                if (connection != null)
                {
                    connection.Close(); connection.GetType().GetMethod("ClearPool")!.Invoke(null, new object[] { connection }); connection.Dispose();
                }
                context.Unload();
            }
        }
    }
}
