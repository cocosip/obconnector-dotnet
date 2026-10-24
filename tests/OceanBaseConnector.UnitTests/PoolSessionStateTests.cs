using System.Collections.Generic;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PoolSessionStateTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var oracle in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                {
                    foreach (var sql in new[]
                    {
                        oracle ? "/* trace */ ALTER\nSESSION SET TIME_ZONE = '+00:00'" : "# trace\nSET SESSION time_zone = '+00:00'",
                        "CREATE /* gap */ GLOBAL\nTEMPORARY TABLE t (id INT)",
                        "CALL p()",
                        oracle ? "<<block_label>> BEGIN p(); END;" : "/*!50000 SET @state = 1 */",
                        oracle ? "DECLARE n NUMBER; BEGIN p(); END;" : "SELECT @state := 1",
                        oracle ? "EXECUTE IMMEDIATE 'ALTER SESSION SET x = 1'" : "SELECT GET_LOCK('state', 0)",
                        oracle ? "BEGIN p(); END;" : "SELECT TRIM(BOTH 'x' FROM GET_LOCK('state', 0))",
                    }) yield return new object[] { oracle, asynchronous, sql, true };
                    foreach (var sql in new[]
                    {
                        "SELECT 'SET x = 1; CALL p()'",
                        "/* SET x = 1 */ SELECT 1",
                        "UPDATE t SET value = 'CREATE TEMPORARY TABLE t'",
                        "SELECT get_lock AS release_lock FROM release_all_locks",
                        "INSERT INTO tenant /* gap */ . get_lock (id) VALUES (1)",
                        oracle ? "SELECT NQ'[it's; SET x = 1]' FROM dual" : "SELECT `SET; CALL` FROM t",
                    }) yield return new object[] { oracle, asynchronous, sql, false };
                }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task BorrowerBReceivesANewOrResetSessionAccordingToExecutedSql(bool oracle, bool asynchronous, string sql, bool discard)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle,
                sessionStateOk: oracle ? TextCommandTests.TimeZoneOk("+00:00") : null);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1");
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                var first = connection.GetOpenSession();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                Assert.Equal(discard, first.RequiresDiscardOnReturn);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                var second = connection.GetOpenSession();
                if (discard) Assert.NotSame(first, second); else Assert.Same(first, second);
                Assert.Equal(discard ? 2 : 1, server.Accepted);
                Assert.False(second.RequiresDiscardOnReturn);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT 1";
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }
    }
}
