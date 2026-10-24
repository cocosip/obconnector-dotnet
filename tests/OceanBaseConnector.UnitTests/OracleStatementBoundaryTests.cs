using System;
using System.Data;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("BEGIN NULL; END;")]
        [InlineData("-- block\nDECLARE n NUMBER := 1; BEGIN IF n = 1 THEN NULL; END IF; END;")]
        [InlineData("DECLARE PROCEDURE p IS BEGIN NULL; END; BEGIN p; END;")]
        [InlineData("<<outer_block>> BEGIN BEGIN NULL; END; END outer_block;")]
        [InlineData("<<\"a>>b\">> BEGIN NULL; END \"a>>b\";")]
        [InlineData("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;")]
        [InlineData("CREATE /* comment */ OR REPLACE NONEDITIONABLE FUNCTION f RETURN NUMBER AS BEGIN RETURN 1; END;")]
        [InlineData("CREATE PACKAGE BODY pkg AS PROCEDURE p IS BEGIN NULL; END; END pkg;")]
        [InlineData("CREATE TRIGGER t BEFORE INSERT ON tab BEGIN NULL; END;")]
        [InlineData("CREATE TYPE BODY t AS MEMBER PROCEDURE p IS BEGIN NULL; END; END;")]
        [InlineData("select q'[it's; :not_a_binding]' from dual")]
        [InlineData("select nq'{it's; :not_a_binding}' from dual")]
        [InlineData("select 'a\\'; --tail_without_space\n")]
        [InlineData("select 1; --tail_without_space\n")]
        public async Task OracleStatementUnitsDoNotRequireMultiStatementCapability(string sql)
        {
            var server = Server(async channel =>
            {
                for (var i = 0; i < 2; i++)
                {
                    await Query(channel, sql);
                    await channel.WritePacketAsync(Ok(1));
                    channel.BeginCommand();
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                Assert.Equal(1, command.ExecuteNonQuery());
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("select 'a\\'; select 2")]
        [InlineData("select col#name; select 2")]
        [InlineData("select 1; --comment\nselect 2")]
        [InlineData("create table t (n number); select 2")]
        [InlineData("beginning; select 2")]
        public async Task OracleSeparateSqlStatementsStillRequireExplicitOptIn(string sql)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "after rejection");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync());
                command.CommandText = "after rejection";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task OracleProgramUnitRecognitionDoesNotChangeMySqlBoundaries()
        {
            const string sql = "select 'a\\'; hidden'; # comment\n";
            var server = Server(async channel =>
            {
                await Query(channel, sql);
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "BEGIN NULL; END;";
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                command.CommandText = sql;
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
