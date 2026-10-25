using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("CREATE TYPE T AS OBJECT (A NUMBER)", true)]
        [InlineData("/*hint*/ create or replace noneditionable type T as table of raw(8)", true)]
        [InlineData("--line\nALTER /*comment*/ TYPE T ADD ATTRIBUTE A NUMBER", true)]
        [InlineData("DROP TYPE T FORCE", true)]
        [InlineData("RENAME T TO U", true)]
        [InlineData("TRUNCATE TABLE T", true)]
        [InlineData("SELECT q'[; DROP TYPE T]' FROM dual; /* gap */ ALTER TYPE T COMPILE", true)]
        [InlineData("SELECT nq'{; CREATE TYPE T;}' FROM dual; DROP TYPE T", true)]
        [InlineData("SELECT \"DROP;TYPE\" FROM dual;\n-- next\nCREATE TYPE T AS TABLE OF NUMBER", true)]
        [InlineData("; /* empty */ ; ALTER TYPE T COMPILE", true)]
        [InlineData("SELECT 'DROP TYPE T; ALTER TYPE T' FROM dual", false)]
        [InlineData("SELECT nq'{; ALTER TYPE T;}' FROM dual", false)]
        [InlineData("SELECT 1 FROM dual; -- DROP TYPE T\n /* CREATE TYPE T */", false)]
        [InlineData("SELECT \"a;DROP TYPE T\" FROM dual", false)]
        [InlineData("CALL pkg.p()", false)]
        [InlineData("BEGIN EXECUTE IMMEDIATE 'DROP TYPE T'; END;", false)]
        [InlineData("<<label>> DECLARE n NUMBER; BEGIN n := 1; END;", false)]
        [InlineData("ALTER_TYPE T", false)]
        [InlineData("\"ALTER\" TYPE T", false)]
        [InlineData("/* unfinished DROP TYPE T", false)]
        public void ComplexDictionaryDdlClassificationHonorsOracleStatementBoundaries(string sql, bool invalidates)
        {
            Assert.Equal(invalidates, SqlStatementBoundary.InvalidatesComplexTypes(sql, OceanBaseCompatibilityMode.Oracle));
            Assert.False(SqlStatementBoundary.InvalidatesComplexTypes(sql, OceanBaseCompatibilityMode.MySql));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryDdlInvalidationWorksWithStatementCaching(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexDictionaryReply(channel, 81, "Owner", "Thing", true, new[] { "Owner", "Thing", "OBJECT", "1" });
                await ComplexDictionaryReply(channel, 82, "Owner", "Thing", false, new[] { "DATA", "1", null, "RAW", "RAW" });
                channel.BeginCommand(); await Query(channel, "DROP TYPE Owner.Thing"); await channel.WritePacketAsync(Ok(1));
                var closes = new[] { (await StatementCommand(channel, 25))[1], (await StatementCommand(channel, 25))[1] };
                Assert.Contains((byte)81, closes); Assert.Contains((byte)82, closes);
                await ComplexDictionaryReply(channel, 83, "Owner", "Thing", true, new[] { "Owner", "Thing", "OBJECT", "1" });
                await ComplexDictionaryReply(channel, 84, "Owner", "Thing", false, new[] { "DATA", "1", null, "NUMBER", "NUMBER" });
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Statement Cache Size=4");
            try
            {
                await connection.OpenAsync(); var old = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "DROP TYPE Owner.Thing";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Throws<InvalidOperationException>(() => old.CreateValue(new byte[] { 1 }));
                var fresh = asynchronous ? await connection.GetComplexTypeAsync("Owner", "Thing") : connection.GetComplexType("Owner", "Thing");
                Assert.NotSame(old, fresh); Assert.Equal("NUMBER", fresh.Members[0].TypeName);
                Assert.Equal(7, fresh.CreateValue(7).GetValues()[0]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("prepare", false)]
        [InlineData("prepare", true)]
        [InlineData("prepare-error", false)]
        [InlineData("prepare-error", true)]
        [InlineData("schema-only", false)]
        [InlineData("schema-only", true)]
        [InlineData("canceled", false)]
        [InlineData("canceled", true)]
        [InlineData("binding-error", false)]
        [InlineData("binding-error", true)]
        public async Task ComplexDictionaryUnsentDdlPreservesDescriptors(string kind, bool asynchronous)
        {
            const string ddl = "CREATE OR REPLACE TYPE Owner.Thing AS TABLE OF NUMBER";
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                if (kind == "prepare" || kind == "prepare-error")
                {
                    Assert.Equal(OceanBaseCommand.SqlPacket(ddl, 22), await StatementCommand(channel, 22));
                    if (kind == "prepare-error") await channel.WritePacketAsync(Error());
                    else { await Prepared(channel, 93, 0); await StatementCommand(channel, 25); }
                }
                channel.BeginCommand(); await Query(channel, "select 1 from dual"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = ddl;
                    if (kind == "prepare") { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    else if (kind == "prepare-error")
                        await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.PrepareAsync() : Task.Run(command.Prepare));
                    else if (kind == "schema-only")
                        await Assert.ThrowsAsync<NotSupportedException>(async () =>
                        {
                            using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly) : command.ExecuteReader(CommandBehavior.SchemaOnly);
                        });
                    else if (kind == "canceled")
                    {
                        using var cancel = new CancellationTokenSource(); cancel.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteNonQueryAsync(cancel.Token));
                    }
                    else
                    {
                        command.CommandText += " :missing";
                        await Assert.ThrowsAsync<ArgumentException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    }
                }
                Assert.Same(type, connection.GetComplexType("Owner", "Thing"));
                Assert.Equal(new byte[] { 3 }, type.CreateValue(new byte[] { 3 }).GetValues()[0]);
                using var probe = connection.CreateCommand(); probe.CommandText = "select 1 from dual";
                Assert.Equal(1, await probe.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("SELECT 'ALTER TYPE T; DROP TYPE T' FROM dual")]
        [InlineData("SELECT q'[; CREATE TYPE T AS OBJECT(a NUMBER);]' FROM dual")]
        [InlineData("SELECT nq'{; ALTER TYPE T ADD ATTRIBUTE b NUMBER;}' FROM dual")]
        [InlineData("SELECT 1 /* ; DROP TYPE T */ FROM dual -- ; ALTER TYPE T\n")]
        [InlineData("BEGIN EXECUTE IMMEDIATE 'ALTER TYPE T ADD ATTRIBUTE b NUMBER'; END;")]
        [InlineData("DECLARE n NUMBER; BEGIN n := 1; END;")]
        [InlineData("CALL Owner.p()")]
        public async Task ComplexDictionarySafeAndOpaqueSqlDoesNotClaimTypeDdlDetection(string sql)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                channel.BeginCommand(); await Query(channel, sql); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = sql;
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                Assert.Same(type, await connection.GetComplexTypeAsync("Owner", "Thing"));
                Assert.Equal(new byte[] { 7 }, type.CreateValue(new byte[] { 7 }).GetValues()[0]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task ComplexDictionaryDdlInvalidatesOldBindingsAndReloadsChangedLayout(bool asynchronous, bool prepared, bool rejected)
        {
            const string ddl = "/* observed type DDL */ ALTER TYPE Owner.Thing ADD ATTRIBUTE extra NUMBER CASCADE";
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                if (prepared)
                {
                    var request = await StatementCommand(channel, 22);
                    Assert.Equal(OceanBaseCommand.SqlPacket(ddl, 22), request);
                    await Prepared(channel, 91, 0);
                    Assert.Equal(PreparedParameters.Execute(91, Array.Empty<byte>()), await StatementCommand(channel, 23));
                }
                else { channel.BeginCommand(); await Query(channel, ddl); }
                await channel.WritePacketAsync(rejected ? Error() : Ok(1));
                if (prepared) await StatementCommand(channel, 25);
                await ComplexScalarType(channel, scalar: "NUMBER");
                await StatementCommand(channel, 22); await Prepared(channel, 92, 1);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok(2));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var old = await connection.GetComplexTypeAsync("Owner", "Thing");
                var value = old.CreateValue(new byte[] { 1, 2 });
                var array = connection.CreateArrayType("RAW");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = ddl;
                    if (prepared)
                    {
                        if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                        Assert.Same(old, await connection.GetComplexTypeAsync("Owner", "Thing"));
                    }
                    if (rejected)
                        await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    else Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                Assert.Throws<InvalidOperationException>(() => old.CreateValue(new byte[] { 3 }));
                Assert.Throws<InvalidOperationException>(() => array.CreateValue(new byte[] { 4 }));
                Assert.Equal(new byte[] { 1, 2 }, value.GetValues()[0]);
                using (var stale = connection.CreateCommand())
                {
                    stale.CommandText = "select :value from dual";
                    stale.Parameters.Add(new OceanBaseParameter { ParameterName = "value", Value = value });
                    await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? stale.ExecuteNonQueryAsync() : Task.FromResult(stale.ExecuteNonQuery()));
                    stale.Parameters[0].Value = DBNull.Value; ((OceanBaseParameter)stale.Parameters[0]).ComplexType = old;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? stale.ExecuteNonQueryAsync() : Task.FromResult(stale.ExecuteNonQuery()));
                }
                var fresh = asynchronous ? await connection.GetComplexTypeAsync("Owner", "Thing") : connection.GetComplexType("Owner", "Thing");
                Assert.NotSame(old, fresh); Assert.Equal("NUMBER", fresh.Members[0].TypeName);
                using var valid = connection.CreateCommand(); valid.CommandText = "select :value from dual";
                valid.Parameters.Add(new OceanBaseParameter { ParameterName = "value", Value = fresh.CreateValue(42) });
                Assert.Equal(2, asynchronous ? await valid.ExecuteNonQueryAsync() : valid.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
