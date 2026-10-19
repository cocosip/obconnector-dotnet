using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static ResultColumn JsonColumn(ushort charset = 63, OceanBaseCompatibilityMode mode = OceanBaseCompatibilityMode.MySql, byte scale = 0)
            => ResultColumn.Parse(SchemaColumn("json", 245, uint.MaxValue, scale: scale, charset: charset), mode);

        [Theory]
        [InlineData(63)]
        [InlineData(33)]
        [InlineData(45)]
        [InlineData(46)]
        [InlineData(83)]
        [InlineData(224)]
        [InlineData(255)]
        public void JsonMetadataAndNullUseStringIdentityEvenWithBinaryCollation(ushort charset)
        {
            var column = JsonColumn(charset);
            Assert.Equal(typeof(string), column.FieldType); Assert.Equal(typeof(string), column.ProviderSpecificFieldType);
            Assert.Equal("JSON", column.DataTypeName); Assert.Equal(OceanBaseDbType.Json, column.ProviderType);
            Assert.Equal(DBNull.Value, column.Decode(null)); Assert.Equal(DBNull.Value, column.DecodeBinary(null));
            var schema = new ResultDbColumn(column, 0);
            Assert.Equal(typeof(string), schema.DataType); Assert.Equal("JSON", schema.DataTypeName);
            Assert.Equal(typeof(string), schema["ProviderSpecificDataType"]); Assert.Equal(7, schema["ProviderType"]);
            Assert.True(schema.IsLong); Assert.Null(schema.ColumnSize); Assert.Null(schema.NumericPrecision); Assert.Null(schema.NumericScale);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"\"")]
        [InlineData("true")]
        [InlineData("[null, false, 0, \"中😀\"]")]
        [InlineData(" {\"n\":1234567890123456789012345678901234567890.12345678901234567890,\"e\":1e300} ")]
        [InlineData("{\"escaped\":\"\\u0000\\n\\\\\\\"\",\"重复\":1,\"重复\":2}")]
        public void JsonBothRowProtocolsAndParametersPreserveTextExactly(string json)
        {
            var column = JsonColumn(); var bytes = Encoding.UTF8.GetBytes(json);
            Assert.Equal(json, column.Decode(bytes));
            var cursor = new ByteCursor(WithLength(bytes).Concat(new byte[] { 99 }).ToArray());
            Assert.Equal(json, column.DecodeBinary(column.ReadBinaryField(cursor))); Assert.Equal(99, cursor.ReadUInt8());
            var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = json };
            Assert.Equal(DbType.String, parameter.DbType);
            Assert.Equal(new byte[] { 0, 1, 253, 0 }.Concat(WithLength(bytes)), PreparedParameters.Encode(new[] { parameter }));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(8, OceanBaseCompatibilityMode.MySql, 0)]
        [InlineData(63, OceanBaseCompatibilityMode.Oracle, 0)]
        [InlineData(45, OceanBaseCompatibilityMode.Oracle, 0)]
        public void JsonUnsupportedColumnMetadataRejectsNullToo(ushort charset, OceanBaseCompatibilityMode mode, byte scale)
        {
            var column = JsonColumn(charset, mode, scale);
            Assert.Throws<NotSupportedException>(() => column.Decode(null));
            Assert.Throws<NotSupportedException>(() => column.DecodeBinary(null));
            Assert.Throws<NotSupportedException>(() => new ResultDbColumn(column, 0));
        }

        [Fact]
        public void JsonDoesNotInterpretNonNumericColumnScaleAsPrecision()
        {
            foreach (var scale in new byte[] { 0, 31, 255 })
            {
                var column = JsonColumn(scale: scale);
                Assert.Equal("null", column.Decode(Encoding.UTF8.GetBytes("null")));
                Assert.Equal(DBNull.Value, column.DecodeBinary(null));
                Assert.Null(new ResultDbColumn(column, 0).NumericScale);
            }
        }

        [Fact]
        public void JsonUsesStrictUtf8AndDoesNotParseOrSerializeClientObjects()
        {
            foreach (var bytes in new[] { new byte[] { 255 }, new byte[] { 34, 192, 128, 34 }, new byte[] { 34, 240, 159, 34 } })
            {
                Assert.Throws<DecoderFallbackException>(() => JsonColumn().Decode(bytes));
                Assert.Throws<DecoderFallbackException>(() => JsonColumn().DecodeBinary(bytes));
            }
            foreach (var value in new object[] { true, 1, 1m, new byte[] { 123, 125 }, new object() })
                Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = value } }));
            var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = "\ud800" };
            Assert.Throws<EncoderFallbackException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = "not json";
            Assert.Equal(new byte[] { 0, 1, 253, 0, 8, 110, 111, 116, 32, 106, 115, 111, 110 }, PreparedParameters.Encode(new[] { parameter }));
            parameter.ResetDbType(); Assert.Null(parameter.OceanBaseDbType); Assert.Equal(DbType.String, parameter.DbType);
            Assert.Equal(13, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle).Length);
        }

        [Fact]
        public void JsonTypedNullsStillValidateModePrecisionAndScale()
        {
            foreach (var value in new object?[] { null, DBNull.Value, "null" })
            {
                var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = value };
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                if (!(value is string)) Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }));
                parameter.Precision = 1; Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
                parameter.Precision = 0; parameter.Scale = 1; Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            }
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public async Task JsonReaderWriteBackAndMultipleResultsPreserveTextAndSqlNull(bool asynchronous, bool binary, bool sequential)
        {
            var documents = new[] { "null", "\"\"", "[true,false,null]", " {\"text\":\"中😀\\u0000\",\"n\":1234567890123456789012345678901234567890.123456789} " };
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 120, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "json");
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(SchemaColumn("j", 245, uint.MaxValue, charset: 63));
                await channel.WritePacketAsync(SchemaColumn("tail", 253, 4)); await channel.WritePacketAsync(Eof());
                foreach (var json in documents)
                {
                    var field = WithLength(Encoding.UTF8.GetBytes(json));
                    await channel.WritePacketAsync((binary ? new byte[] { 0, 0 } : Array.Empty<byte>()).Concat(field).Concat(new byte[] { 1, 120 }).ToArray());
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4, 1, 120 } : new byte[] { 251, 1, 120 });
                await channel.WritePacketAsync(Eof(14));
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(SchemaColumn("after", 253, 4));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(binary ? new byte[] { 0, 0, 1, 121 } : new byte[] { 1, 121 });
                await channel.WritePacketAsync(Eof()); if (binary) await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 121, 1);
                foreach (var json in documents)
                {
                    var execute = await StatementCommand(channel, 23);
                    Assert.Equal(new byte[] { 0, 1, 253, 0 }.Concat(WithLength(Encoding.UTF8.GetBytes(json))), execute.AsSpan(10).ToArray());
                    await channel.WritePacketAsync(Ok(1));
                }
                var nullExecute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 1, 1, 6, 0 }, nullExecute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "reuse"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "json";
                    if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    var schema = reader.GetColumnSchema()[0]; var table = reader.GetSchemaTable()!;
                    Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Equal(typeof(string), reader.GetProviderSpecificFieldType(0));
                    Assert.Equal("JSON", reader.GetDataTypeName(0)); Assert.Equal(7, schema["ProviderType"]); Assert.True(schema.IsLong);
                    Assert.Null(schema.ColumnSize); Assert.Equal(DBNull.Value, table.Rows[0]["ColumnSize"]);
                    Assert.Equal(typeof(string), table.Rows[0]["ProviderSpecificDataType"]);
                    foreach (var json in documents)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(reader.IsDBNull(0));
                        Assert.Equal(json, asynchronous ? await reader.GetFieldValueAsync<string>(0) : reader.GetString(0));
                        Assert.Equal(json, reader.GetValue(0)); Assert.Equal(json, reader.GetProviderSpecificValue(0));
                        Assert.Equal("x", reader.GetString(1));
                    }
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
                    Assert.Equal(DBNull.Value, reader.GetValue(0)); Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                    Assert.Equal("x", reader.GetString(1)); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal("VARCHAR", reader.GetDataTypeName(0));
                    Assert.Equal("JSON", schema.DataTypeName); Assert.Equal(7, schema["ProviderType"]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("y", reader.GetString(0));
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select ?";
                    var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json }; command.Parameters.Add(parameter);
                    foreach (var json in documents)
                    {
                        parameter.Value = json; Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    }
                    parameter.Value = DBNull.Value; Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse"; Assert.Equal(1, await reuse.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task JsonInvalidParametersRejectBeforePrepareAndServerSyntaxErrorPreservesSession(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 122, 1);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 253, 0, 8, 110, 111, 116, 32, 106, 115, 111, 110 }, execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(new byte[] { 255, 68, 12, 35, 50, 50, 48, 51, 50, 105, 110, 118, 97, 108, 105, 100 });
                var retry = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 253, 0, 4, 110, 117, 108, 108 }, retry.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select ?";
                var parameter = new OceanBaseParameter { ParameterName = "j", OceanBaseDbType = OceanBaseDbType.Json, Value = DBNull.Value };
                command.Parameters.Add(parameter);
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<NotSupportedException>(execute); UseInternalMySqlFixture(connection);
                parameter.Precision = 1; await Assert.ThrowsAsync<NotSupportedException>(execute); parameter.Precision = 0;
                parameter.Value = new byte[] { 123, 125 }; await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = "\ud800"; await Assert.ThrowsAsync<EncoderFallbackException>(execute);
                parameter.Value = "not json";
                var error = await Assert.ThrowsAsync<OceanBaseException>(execute);
                Assert.Equal(3140, error.ServerErrorCode); Assert.Equal("22032", error.SqlState); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Open, connection.State); parameter.Value = "null";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public async Task JsonSequentialTextStreamKeepsStrictUtf8AndFieldLifetime(bool asynchronous, bool binary, bool malformed)
        {
            var json = "{\"value\":\"" + new string('x', 8193) + "中😀\\u0000\"}";
            var bytes = malformed ? new byte[] { 34, 192, 128, 34 } : Encoding.UTF8.GetBytes(json);
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 123, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "stream json");
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(SchemaColumn("j", 245, uint.MaxValue, charset: 63));
                await channel.WritePacketAsync(SchemaColumn("tail", 253, 4)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync((binary ? new byte[] { 0, 0 } : Array.Empty<byte>()).Concat(WithLength(bytes)).Concat(new byte[] { 1, 120 }).ToArray());
                await channel.WritePacketAsync(Eof()); if (binary) await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "reuse"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "stream json";
                    if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Throws<InvalidCastException>(() => reader.GetStream(0));
                    if (!malformed)
                    {
                        var chars = new char[3]; Assert.Equal(3, reader.GetChars(0, 0, chars, 0, 3));
                        Assert.Equal(json.Substring(0, 3), new string(chars));
                    }
                    using var text = asynchronous ? await reader.GetFieldValueAsync<TextReader>(0) : reader.GetTextReader(0);
                    if (malformed)
                    {
                        if (asynchronous) await Assert.ThrowsAsync<DecoderFallbackException>(() => text.ReadToEndAsync());
                        else Assert.Throws<DecoderFallbackException>(() => text.ReadToEnd());
                    }
                    else Assert.Equal(json.Substring(3), asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
                    Assert.Equal("x", reader.GetString(1)); Assert.Throws<InvalidOperationException>(() => text.Read());
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse"; Assert.Equal(1, await reuse.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
