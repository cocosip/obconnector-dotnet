using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("NUMBER", (byte)246, "0100023432")]
        [InlineData("VARCHAR2", (byte)253, "01000AE682A3E88085F09F9880")]
        [InlineData("CHAR", (byte)254, "01000161")]
        [InlineData("RAW", (byte)203, "01000200FF")]
        [InlineData("DATE", (byte)12, "010007E8070203040506")]
        [InlineData("TIMESTAMP", (byte)202, "01000C1418020304050615CD5B0709")]
        public async Task AnonymousArrayFactoryConstructsExactScalarInputs(string name, byte element, string body)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType(name.ToLowerInvariant());
                Assert.True(type.IsAnonymous); Assert.True(type.IsCollection); Assert.Equal("", type.SchemaName); Assert.Equal("", type.TypeName);
                Assert.Equal(name, Assert.Single(type.Members).TypeName); Assert.Null(type.Members[0].ComplexType);
                object item = name == "NUMBER" ? 42 : name == "VARCHAR2" ? "患者😀" : name == "CHAR" ? "a" : name == "RAW" ? new byte[] { 0, 255 }
                    : name == "DATE" ? new DateTime(2024, 2, 3, 4, 5, 6) : new OceanBaseTimestamp(new DateTime(2024, 2, 3, 4, 5, 6), 123456789, 9);
                var value = type.CreateValue(item);
                Assert.Throws<ArgumentException>(() => type.CreateValue(new object()));
                var header = new byte[] { 0, 1, 160, 0, 0, 0, 0, element };
                Assert.Equal(header.Concat(Convert.FromHexString(body)), PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } }, OceanBaseCompatibilityMode.Oracle));
                Assert.Equal(header.Concat(new byte[] { 0 }), PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = type.CreateValue() } }, OceanBaseCompatibilityMode.Oracle));
                var nullHeader = (byte[])header.Clone(); nullHeader[0] = 1;
                Assert.Equal(nullHeader, PreparedParameters.Encode(new[] { new OceanBaseParameter { ComplexType = type, Value = DBNull.Value } }, OceanBaseCompatibilityMode.Oracle));
                Assert.Equal(header.Concat(new byte[] { 2, 3 }), PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = type.CreateValue(null, DBNull.Value) } }, OceanBaseCompatibilityMode.Oracle));
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } }, OceanBaseCompatibilityMode.MySql));
                foreach (var direction in new[] { ParameterDirection.Output, ParameterDirection.InputOutput, ParameterDirection.ReturnValue })
                {
                    using var command = new OceanBaseCommand { CommandText = "pkg.p", CommandType = CommandType.StoredProcedure };
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value, Direction = direction });
                    var plan = OracleCommandPlan.Create(command.CommandText, command.CommandType, (OceanBaseParameterCollection)command.Parameters);
                    Assert.Same(type, Assert.Single(plan.Bindings.Parameters).ComplexType);
                }
                connection.ClearComplexTypeCache(); Assert.Throws<InvalidOperationException>(() => type.CreateValue(item));
                Assert.Single(value.GetValues());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnonymousArrayOfNamedElementsKeepsFullIdentityAndExactNestedLayout(bool collection)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, collection: collection); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var element = await connection.GetComplexTypeAsync("Owner", "Thing");
                var type = connection.CreateArrayType(element); Assert.Same(element, Assert.Single(type.Members).ComplexType);
                var nested = element.CreateValue(new byte[] { 42 }); var value = type.CreateValue(nested, null);
                var header = new byte[] { 0, 1, 160, 0, 0, 0, 0, 160, 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0 };
                var expected = header.Concat(new byte[] { 2, 2 }).Concat(collection ? new byte[] { 1, 0, 1, 42 } : new byte[] { 0, 1, 42 });
                Assert.Equal(expected, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } }, OceanBaseCompatibilityMode.Oracle));
                Assert.Throws<ArgumentException>(() => type.CreateValue(new object?[] { new object?[] { new byte[] { 42 } } }));
                Assert.Throws<NotSupportedException>(() => connection.CreateArrayType(type));
                foreach (var direction in new[] { ParameterDirection.Output, ParameterDirection.InputOutput, ParameterDirection.ReturnValue })
                {
                    var parameters = new OceanBaseParameterCollection();
                    parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value, Direction = direction });
                    Assert.Throws<NotSupportedException>(() => OracleCommandPlan.Create("pkg.p", CommandType.StoredProcedure, parameters));
                }
                connection.ClearComplexTypeCache(); Assert.Throws<InvalidOperationException>(() => type.CreateValue(nested));
                Assert.Throws<InvalidOperationException>(() => connection.CreateArrayType(element));
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" NUMBER")]
        [InlineData("NUMBER(9)")]
        [InlineData("CURSOR")]
        [InlineData("NVARCHAR2(4)")]
        [InlineData("Owner.Thing")]
        [InlineData("\0")]
        public void AnonymousArrayFactoryDoesNotInferUnsupportedScalarNames(string name)
        {
            using var connection = new OceanBaseConnection();
            Assert.Throws<NotSupportedException>(() => connection.CreateArrayType(name));
        }

        [Theory]
        [InlineData("RAW")]
        [InlineData("BLOB")]
        [InlineData("CLOB")]
        public void AnonymousArrayFactoryRequiresAnOpenConnectionAndNonNullElement(string scalar)
        {
            using var connection = new OceanBaseConnection();
            Assert.Throws<ArgumentNullException>(() => connection.CreateArrayType((string)null!));
            Assert.Throws<ArgumentNullException>(() => connection.CreateArrayType((OceanBaseComplexType)null!));
            Assert.Throws<InvalidOperationException>(() => connection.CreateArrayType(scalar));
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
        public async Task AnonymousArrayInputsExecuteWithHeadersBeforeValuesAndTypedNull(bool async, bool extended, bool named)
        {
            var server = Server(async channel =>
            {
                if (named) { await ComplexScalarType(channel, collection: false); channel.BeginCommand(); }
                if (!extended) { await StatementCommand(channel, 22); await Prepared(channel, 203, 3); }
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    var request = await StatementCommand(channel, extended ? (byte)0xa1 : (byte)23);
                    var header = new byte[] { 160, 0, 0, 0, 0, named ? (byte)160 : (byte)203 }
                        .Concat(named ? new byte[] { 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0 } : Array.Empty<byte>()).ToArray();
                    var encoded = new byte[] { iteration == 0 ? (byte)4 : (byte)6, 1, 3, 0 }.Concat(header).Concat(header).Concat(new byte[] { 7, 0, 0, 0 })
                        .Concat(iteration == 1 ? Array.Empty<byte>() : named ? new byte[] { 2, 2, 0, 1, 42 } : new byte[] { 2, 2, 1, 42 }).ToArray();
                    Assert.Equal(extended ? OraclePrepareExecutePacket.Build(0, "BEGIN pkg.p(?, ?, ?); END;", 3, encoded) : PreparedParameters.Execute(203, encoded), request);
                    if (extended)
                    {
                        await channel.WritePacketAsync(OracleExecuteHeader(3, 0, 0, rows: false));
                        for (var i = 0; i < 3; i++) await channel.WritePacketAsync(Column("p", 253));
                        await channel.WritePacketAsync(Eof());
                    }
                    await channel.WritePacketAsync(Ok((byte)(iteration + 1)));
                    if (extended) await StatementCommand(channel, 25);
                }
                if (!extended) await StatementCommand(channel, 25);
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + (extended ? ";Use Oracle Prepare Execute=true" : ""));
            try
            {
                await connection.OpenAsync();
                var element = named ? await connection.GetComplexTypeAsync("Owner", "Thing") : null;
                var type = named ? connection.CreateArrayType(element!) : connection.CreateArrayType("RAW");
                var raw = new byte[] { 42 }; var value = type.CreateValue(named ? (object)element!.CreateValue(raw) : raw, null); raw[0] = 99;
                using var command = connection.CreateCommand(); command.CommandText = extended ? "pkg.p" : "insert into t values(:n, :p, :q)";
                command.CommandType = extended ? CommandType.StoredProcedure : CommandType.Text;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = 7 });
                var parameter = new OceanBaseParameter { ParameterName = "p", Value = value }; command.Parameters.Add(parameter);
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "q", ComplexType = type, Value = DBNull.Value });
                Assert.Equal(1, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                parameter.ComplexType = type; parameter.Value = DBNull.Value;
                Assert.Equal(2, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnonymousArrayCacheClearRejectsValuesAndTypedNullBeforeSending(bool async)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(7)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW"); var value = type.CreateValue(new byte[] { 42 });
                connection.ClearComplexTypeCache(); connection.CreateArrayType("RAW");
                using var command = connection.CreateCommand(); command.CommandText = "must not send :p";
                var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type }; command.Parameters.Add(parameter);
                foreach (var item in new object[] { value, DBNull.Value })
                {
                    parameter.Value = item;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.False(((OceanBaseCommand)command).HasSentSql); Assert.Equal(ConnectionState.Open, connection.State);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task AnonymousArrayRejectsForeignOrReopenedPhysicalSessions(bool async, bool reopen)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            var next = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(7)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var foreign = new OceanBaseConnection(Configuration(next.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW"); var value = type.CreateValue(new byte[] { 42 });
                OceanBaseConnection target;
                if (reopen) { connection.Close(); connection.ConnectionString = Configuration(next.Port); await connection.OpenAsync(); target = connection; }
                else { await foreign.OpenAsync(); target = foreign; }
                target.CreateArrayType("NUMBER");
                using var command = target.CreateCommand(); command.CommandText = "must not send :p";
                var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type }; command.Parameters.Add(parameter);
                foreach (var item in new object[] { value, DBNull.Value })
                {
                    parameter.Value = item;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.False(((OceanBaseCommand)command).HasSentSql); Assert.Equal(ConnectionState.Open, target.State);
                }
                using var after = target.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); foreign.Close(); server.Stop(); next.Stop(); }
            await Task.WhenAll(server.Completion, next.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnonymousArrayRejectsBothTransactionEpochChangesBeforeSending(bool async)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await TransactionEnd(channel, false);
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW"); var before = type.CreateValue(new byte[] { 1 });
                using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                using var command = connection.CreateCommand(); command.CommandText = "must not send :p"; command.Transaction = transaction;
                var parameter = new OceanBaseParameter { ParameterName = "p", Value = before }; command.Parameters.Add(parameter);
                await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                var current = connection.CreateArrayType("RAW", transaction); parameter.Value = current.CreateValue(new byte[] { 2 });
                await transaction.RollbackAsync(); command.Transaction = null;
                await Assert.ThrowsAsync<InvalidOperationException>(() => async ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Throws<InvalidOperationException>(() => current.CreateValue()); Assert.False(((OceanBaseCommand)command).HasSentSql);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
