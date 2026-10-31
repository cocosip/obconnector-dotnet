using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Fact]
        public void NamedBlobObjectPayloadPreservesRawBytesAtEachObjectLevel()
        {
            var blob = new Internal.Values.ComplexType(210);
            var flat = new Internal.Values.ComplexType(false, blob);
            var nested = new Internal.Values.ComplexType(false, new Internal.Values.ComplexType(false, blob));
            var bytes = new byte[] { 0, 255, 23 };

            Assert.Equal(new byte[] { 0, 3, 0, 255, 23 }, Internal.Values.ComplexValueCodec.EncodeParameter(
                flat, new object?[] { bytes }, OceanBaseCompatibilityMode.Oracle));
            Assert.Equal(new byte[] { 0, 0, 3, 0, 255, 23 }, Internal.Values.ComplexValueCodec.EncodeParameter(
                nested, new object?[] { new object?[] { bytes } }, OceanBaseCompatibilityMode.Oracle));
            Assert.Equal(new byte[] { 0, 0 }, Internal.Values.ComplexValueCodec.EncodeParameter(
                flat, new object?[] { Array.Empty<byte>() }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData("gb18030", false, false)]
        [InlineData("gb18030", true, false)]
        [InlineData("gb18030", false, true)]
        [InlineData("gb18030", true, true)]
        [InlineData("latin1", false, false)]
        [InlineData("latin1", true, false)]
        [InlineData("latin1", false, true)]
        [InlineData("latin1", true, true)]
        public void NamedClobPayloadUsesUtf8WithoutClientCharsetConversion(string charset, bool collection, bool nested)
        {
            var leaf = new Internal.Values.ComplexType(collection, new Internal.Values.ComplexType(211));
            var layout = nested ? new Internal.Values.ComplexType(false, leaf) : leaf;
            var text = "患者😀\\'";
            object?[] values = nested ? new object?[] { new object?[] { text } } : new object?[] { text };
            var prefix = (nested ? new byte[] { 0 } : Array.Empty<byte>())
                .Concat(collection ? new byte[] { 1, 0 } : new byte[] { 0 });
            var expected = prefix.Concat(WithLength(Encoding.UTF8.GetBytes(text))).ToArray();
            Assert.Equal(expected, Internal.Values.ComplexValueCodec.EncodeParameter(layout, values,
                OceanBaseCompatibilityMode.Oracle, clientEncoding: ClientCharacterSet.Parse(charset)));
            object?[] invalid = nested ? new object?[] { new object?[] { "\ud800" } } : new object?[] { "\ud800" };
            Assert.Throws<EncoderFallbackException>(() => Internal.Values.ComplexValueCodec.EncodeParameter(layout, invalid,
                OceanBaseCompatibilityMode.Oracle, clientEncoding: ClientCharacterSet.Parse(charset)));
        }

        public static IEnumerable<object[]> ComplexLobInputCases()
        {
            foreach (var scalar in new[] { "BLOB", "CLOB" })
                foreach (var shape in new[] { "object", "collection", "array", "nested" })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var prepareExecute in new[] { false, true })
                            yield return new object[] { scalar, shape, asynchronous, prepareExecute };
        }

        [Theory]
        [MemberData(nameof(ComplexLobInputCases))]
        public async Task ComplexLobInputsUseNamedPayloadsAndAnonymousLocators(string scalar, string shape, bool asynchronous, bool prepareExecute)
        {
            var code = scalar == "CLOB" ? (byte)211 : (byte)210;
            var locators = new[] { InlineLob(Encoding.UTF8.GetBytes("患者😀\\'")), InlineLob(Array.Empty<byte>()), ExternalLob(90000) };
            var payloads = new[] { Encoding.UTF8.GetBytes("患者😀\\'"), Array.Empty<byte>(), Encoding.UTF8.GetBytes(new string('x', 90000)) };
            var named = shape != "array";
            var server = Server(async channel =>
            {
                if (named) { await ComplexScalarType(channel, scalar: scalar, collection: shape == "collection"); channel.BeginCommand(); }
                foreach (var locator in locators)
                {
                    await LobQueryReply(channel, code, locator);
                    for (var nullValue = 0; nullValue < 2; nullValue++)
                    {
                        var identity = named ? new byte[] { 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0 } : Array.Empty<byte>();
                        var header = shape == "array" ? new byte[] { 0, 0, 0, code }
                            : shape == "nested" ? new byte[] { 0, 0, 0, 160 }.Concat(identity).ToArray() : identity;
                        var body = shape == "object" ? new byte[] { 0 }
                            : shape == "nested" ? new byte[] { 2, 2, 0 } : new byte[] { 2, 2 };
                        var encoded = new byte[] { (byte)nullValue, 1, 160, 0 }.Concat(header)
                            .Concat(nullValue == 1 ? Array.Empty<byte>() : body.Concat(WithLength(shape == "array" ? locator : payloads[Array.IndexOf(locators, locator)]))).ToArray();
                        if (nullValue == 1 && (shape == "object" || shape == "collection")) encoded = new byte[] { 1, 1, 6, 0 };
                        if (prepareExecute)
                        {
                            var request = await StatementCommand(channel, 0xa1);
                            Assert.Equal(OraclePrepareExecutePacket.Build(0, "BEGIN pkg.p(?); END;", 1, encoded), request);
                            await channel.WritePacketAsync(OracleExecuteHeader(1, 0, 0, false));
                            await channel.WritePacketAsync(Column("p", 253)); await channel.WritePacketAsync(Eof());
                        }
                        else
                        {
                            if (ReferenceEquals(locator, locators[0]) && nullValue == 0)
                            { await StatementCommand(channel, 22); await Prepared(channel, 197, 1); }
                            Assert.Equal(PreparedParameters.Execute(197, encoded), await StatementCommand(channel, 23));
                        }
                        await channel.WritePacketAsync(Ok(1));
                        if (prepareExecute) await StatementCommand(channel, 25);
                        channel.BeginCommand();
                    }
                }
                if (!prepareExecute) await StatementCommand(channel, 25);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync();
                var type = named ? await connection.GetComplexTypeAsync("Owner", "Thing") : connection.CreateArrayType(scalar);
                var root = shape == "nested" ? connection.CreateArrayType(type) : type;
                using var command = connection.CreateCommand();
                command.CommandType = prepareExecute ? CommandType.StoredProcedure : CommandType.Text;
                command.CommandText = prepareExecute ? "pkg.p" : "INSERT INTO t VALUES(:p)";
                var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = root }; command.Parameters.Add(parameter);
                foreach (var locator in locators)
                {
                    using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                    using var lob = Assert.IsType<OceanBaseLob>(asynchronous ? await source.ExecuteScalarAsync() : source.ExecuteScalar());
                    object input = shape == "array" ? lob : code == 211 ? Encoding.UTF8.GetString(payloads[Array.IndexOf(locators, locator)]) : payloads[Array.IndexOf(locators, locator)];
                    parameter.Value = shape == "object" ? type.CreateValue(input)
                        : shape == "nested" ? root.CreateValue(type.CreateValue(input), null) : root.CreateValue(input, null);
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    parameter.Value = DBNull.Value;
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    if (shape == "array") Assert.Same(lob, root.CreateValue(lob).GetValues()[0]);
                    else Assert.Throws<NotSupportedException>(() => type.CreateValue(lob));
                }
            }
            catch
            {
                if (server.Completion.IsFaulted) await server.Completion;
                throw;
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("BLOB", false)]
        [InlineData("BLOB", true)]
        [InlineData("CLOB", false)]
        [InlineData("CLOB", true)]
        public async Task ComplexLobInputsRejectWrongKindDisposedAndReservedHandles(string scalar, bool asynchronous)
        {
            var code = scalar == "CLOB" ? (byte)211 : (byte)210;
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: scalar, collection: false);
                channel.BeginCommand();
                await LobQueryReply(channel, code, InlineLob(new byte[] { 97 }));
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                using var lob = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
                Assert.Throws<ArgumentException>(() => type.CreateValue(new object()));
                Assert.Throws<ArgumentException>(() => type.CreateValue(scalar == "BLOB" ? (object)"wrong kind" : new byte[] { 1 }));
                Assert.Throws<NotSupportedException>(() => type.CreateValue(lob));
                var otherKind = connection.CreateArrayType(scalar == "BLOB" ? "CLOB" : "BLOB");
                Assert.Throws<ArgumentException>(() => otherKind.CreateValue(lob));
                var array = connection.CreateArrayType(scalar); var value = array.CreateValue(lob);
                using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO t VALUES(:p)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = value });
                using (var stream = scalar == "CLOB" ? (IDisposable)lob.OpenTextReader() : lob.OpenRead())
                    await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                lob.Dispose();
                Assert.Throws<ObjectDisposedException>(() => array.CreateValue(lob));
                await Assert.ThrowsAsync<ObjectDisposedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
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
        public async Task ComplexLobInputOutputUsesPayloadInputsAndLocatorOutputs(bool asynchronous, bool character, bool collection)
        {
            var scalar = character ? "CLOB" : "BLOB"; var code = character ? (byte)211 : (byte)210;
            var input = InlineLob(new byte[] { 97 }); var output = InlineLob(new byte[] { 98, 99 });
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: scalar, collection: collection); channel.BeginCommand();
                await LobQueryReply(channel, code, input);
                var prefix = new byte[] { 0, 1, 160, 0, 5, 79, 119, 110, 101, 114, 5, 84, 104, 105, 110, 103, 0 };
                var encoded = prefix.Concat(collection ? new byte[] { 1, 0 } : new byte[] { 0 }).Concat(new byte[] { 1, 97 }).ToArray();
                Assert.Equal(OraclePrepareExecutePacket.Build(0, "BEGIN pkg.p(?); END;", 1, encoded), await StatementCommand(channel, 0xa1));
                await channel.WritePacketAsync(OracleExecuteHeader(1)); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(collection ? new byte[] { 1, 0 } : new byte[] { 0 }).Concat(WithLength(output)).ToArray());
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                using var original = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
                using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, Value = type.CreateValue(character ? (object)"a" : new byte[] { 97 }) };
                command.Parameters.Add(parameter); Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                using var actual = Assert.IsType<OceanBaseLob>(Assert.Single(Assert.IsType<OceanBaseComplexValue>(parameter.Value).GetValues()));
                Assert.NotSame(original, actual); Assert.Equal(2, actual.ByteLength); Assert.Equal(character, actual.IsCharacter);
                Assert.Throws<NotSupportedException>(() => type.CreateValue(original));
                Assert.Equal(input, original.BindingBytes());
                using var stream = character ? (IDisposable)actual.OpenTextReader() : actual.OpenRead();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ComplexLobInputsRejectForeignHandlesAtCreationAndExecution(bool asynchronous, bool reopen)
        {
            var first = Server(channel => LobQueryReply(channel, 210, InlineLob(new byte[] { 97 })), lobLocators: true);
            var second = Server(async channel =>
            {
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var original = new OceanBaseConnection(LobConfiguration(first.Port));
            using var target = new OceanBaseConnection(LobConfiguration(second.Port));
            try
            {
                await original.OpenAsync(); using var source = original.CreateCommand(); source.CommandText = "select lob_value";
                using var lob = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
                if (reopen) { original.Close(); original.ConnectionString = LobConfiguration(second.Port); }
                var destination = reopen ? original : target;
                await destination.OpenAsync(); var type = destination.CreateArrayType("BLOB");
                Assert.Throws<InvalidOperationException>(() => type.CreateValue(lob));
                // Exercise execution independently of the creation guard.
                using var command = destination.CreateCommand(); command.CommandText = "INSERT INTO t VALUES(:p)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = new OceanBaseComplexValue(type, new object?[] { lob }) });
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                using var after = destination.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { original.Close(); target.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("option")]
        [InlineData("capability")]
        public async Task ComplexLobTypedNullRequiresNegotiatedLocators(string missing)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: "BLOB", collection: false);
                channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: missing != "capability");
            using var connection = new OceanBaseConnection(missing == "option" ? Configuration(server.Port) : LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO t VALUES(:p)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = DBNull.Value });
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ComplexLobInputsRejectOldTransactionWithFreshDescriptor(bool asynchronous, bool insideTransaction)
        {
            var server = Server(async channel =>
            {
                if (insideTransaction) await TransactionBegin(channel);
                await LobQueryReply(channel, 210, InlineLob(new byte[] { 97 }));
                if (!insideTransaction) await TransactionBegin(channel);
                await TransactionEnd(channel, false); channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var initial = insideTransaction ? await connection.BeginTransactionAsync() : null;
                using var source = connection.CreateCommand(); source.CommandText = "select lob_value"; source.Transaction = initial;
                using var lob = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
                using var later = insideTransaction ? null : await connection.BeginTransactionAsync();
                await (initial ?? later!).RollbackAsync();
                var type = connection.CreateArrayType("BLOB");
                Assert.Throws<InvalidOperationException>(() => type.CreateValue(lob));
                using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO t VALUES(:p)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = new OceanBaseComplexValue(type, new object?[] { lob }) });
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexLobPrepareAndSchemaOnlyDoNotConsumeDisposedHandles(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, InlineLob(new byte[] { 97 }));
                await StatementCommand(channel, 22); await Prepared(channel, 197, 1, 1);
                await StatementCommand(channel, 25);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                using var lob = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
                var value = connection.CreateArrayType("BLOB").CreateValue(lob); lob.Dispose();
                using var command = connection.CreateCommand(); command.CommandText = "SELECT :p FROM DUAL";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = value });
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly) : command.ExecuteReader(CommandBehavior.SchemaOnly);
                Assert.Equal(1, reader.FieldCount); Assert.False(reader.Read()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ComplexPrepareWithoutLobNegotiationDoesNotValidateUnusedInputMetadata()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 197, 1);
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT :p FROM DUAL";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = connection.CreateArrayType("NUMBER"), Size = 1, Value = "unused" });
                await command.PrepareAsync(); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
