using System;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {

        private static async Task<byte[]> ReadNationalClientCommand(PacketChannel channel, string national, bool deprecated = false)
        {
            channel.BeginCommand();
            var request = await channel.ReadPacketAsync();
            while (request[0] == 3)
            {
                var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                if (sql == SessionTimeZoneSql) await TimeZoneReply(channel, deprecated: deprecated);
                else if (sql == NationalCharacterSetSql) await NationalCharacterSetReply(channel, national, deprecated: deprecated);
                else break;
                channel.BeginCommand(); request = await channel.ReadPacketAsync();
            }
            return request;
        }

        public static IEnumerable<object[]> NationalCases()
        {
            foreach (var name in new[] { "utf8mb4", "utf16" }.Concat(TestInfrastructure.ClientCharacterSetFixture.Samples.Select(s => s.Name)))
                foreach (var async in new[] { false, true })
                    foreach (var binary in new[] { false, true })
                        foreach (var sequential in new[] { false, true }) yield return new object[] { name, async, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(NationalCases))]
        public async Task NationalCharacterPublicReaderAndParametersPreserveUnicode(string name, bool asynchronous, bool binary, bool sequential)
        {
            var text = "\ufeff" + new string('中', 1700) + "\U0001f600  ";
            var encoding = name == "utf16" ? NationalCharacterSet.Utf16 : ResultColumn.Utf8;
            var bytes = WithLength(encoding.GetBytes(text));
            if (name != "utf8mb4" && name != "utf16")
            {
                var sample = TestInfrastructure.ClientCharacterSetFixture.Samples.Single(s => s.Name == name);
                text = new string('A', 1023) + string.Concat(Enumerable.Repeat(sample.Text, 700)) + "'\\\0  ";
                bytes = WithLength(Enumerable.Repeat((byte)65, 1023).Concat(Enumerable.Range(0, 700).SelectMany(_ => Convert.FromHexString(sample.Hex)))
                    .Concat(new byte[] { 39, 92, 0, 32, 32 }).ToArray());
            }
            var server = Server(async channel =>
            {
                var request = await ReadNationalClientCommand(channel, name);
                Assert.Equal(binary ? 22 : 3, request[0]);
                if (binary)
                {
                    await Prepared(channel, 132, 0, 6);
                    Assert.Equal(23, (await ReadNationalClientCommand(channel, name))[0]);
                }
                await channel.WritePacketAsync(new byte[] { 6 });
                foreach (var type in new byte[] { 208, 207, 208, 207, 253, 9 })
                    await channel.WritePacketAsync(SchemaColumn("value", type, 20000, charset: type == 207 || type == 208 ? (ushort)54 : (ushort)45));
                await channel.WritePacketAsync(Eof());
                var row = (binary ? new byte[] { 0, 32 } : Array.Empty<byte>()).Concat(bytes).Concat(bytes).Concat(bytes)
                    .Concat(binary ? Array.Empty<byte>() : new byte[] { 251 }).Concat(new byte[] { 1, 120 })
                    .Concat(binary ? new byte[] { 42, 0, 0, 0 } : new byte[] { 2, 52, 50 }).ToArray();
                await channel.WritePacketAsync(row); await channel.WritePacketAsync(Eof());
                if (binary) Assert.Equal(25, (await ReadNationalClientCommand(channel, name))[0]);
                Assert.Equal(22, (await ReadNationalClientCommand(channel, name))[0]); await Prepared(channel, 133, 2);
                request = await ReadNationalClientCommand(channel, name); Assert.Equal(23, request[0]);
                Assert.Equal(new byte[] { 0, 1, 208, 0, 207, 0 }.Concat(bytes).Concat(bytes), request.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1));
                request = await ReadNationalClientCommand(channel, name);
                Assert.Equal(new byte[] { 3, 1, 6, 0, 6, 0 }, request.AsSpan(10).ToArray()); await channel.WritePacketAsync(Ok(1));
                Assert.Equal(25, (await ReadNationalClientCommand(channel, name))[0]);
                request = await ReadNationalClientCommand(channel, name); Assert.Equal(3, request[0]); await channel.WritePacketAsync(Ok(1));
            }, nationalCharacterSet: name);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "national"; if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    var schema = reader.GetColumnSchema();
                    Assert.Equal("NCHAR", schema[0].DataTypeName); Assert.Equal("NVARCHAR2", schema[1].DataTypeName);
                    Assert.Equal(20000, schema[0].ColumnSize); Assert.Equal(typeof(string), reader.GetProviderSpecificFieldType(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    using (var stream = reader.GetTextReader(0)) Assert.Equal(text, asynchronous ? await stream.ReadToEndAsync() : stream.ReadToEnd());
                    var buffer = new char[text.Length]; Assert.Equal(text.Length, reader.GetChars(1, 0, buffer, 0, buffer.Length)); Assert.Equal(text, new string(buffer));
                    Assert.Equal(text, reader.GetString(2)); Assert.Equal(text, reader.GetProviderSpecificValue(2));
                    Assert.True(reader.IsDBNull(3)); Assert.Equal(DBNull.Value, reader.GetValue(3));
                    Assert.Equal("x", reader.GetString(4)); Assert.Equal(42, reader.GetInt32(5));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select :n, :v";
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = text, OceanBaseDbType = OceanBaseDbType.NChar });
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "v", Value = text, OceanBaseDbType = OceanBaseDbType.NVarchar2 });
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    command.Parameters[0].Value = DBNull.Value; command.Parameters[1].Value = null;
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    if (name == "ascii" || name == "latin1" || name == "utf8mb3")
                    {
                        command.Parameters[0].Value = name == "ascii" ? "中" : "🙂";
                        await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                        Assert.Equal(ConnectionState.Open, connection.State);
                    }
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse"; Assert.Equal(1, await reuse.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(207, OceanBaseDbType.NVarchar2, "NVARCHAR2")]
        [InlineData(208, OceanBaseDbType.NChar, "NCHAR")]
        public void NationalCharacterMetadataKeepsItsIdentity(byte type, OceanBaseDbType provider, string name)
        {
            var column = ResultColumn.Parse(SchemaColumn("n", type, 32, charset: 54), OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(typeof(string), column.FieldType);
            Assert.Equal(typeof(string), column.ProviderSpecificFieldType);
            Assert.Equal(name, column.DataTypeName);
            Assert.Equal(provider, column.ProviderType);
            Assert.Equal(DBNull.Value, column.Decode(null));
            var schema = new ResultDbColumn(column, 0);
            Assert.Equal(32, schema.ColumnSize);
            Assert.Null(schema.NumericPrecision);
            Assert.Null(schema.NumericScale);
        }

        [Theory]
        [InlineData(OceanBaseDbType.NChar)]
        [InlineData(OceanBaseDbType.NVarchar2)]
        public void NationalCharacterParametersKeepStringDbTypeAndValidateMode(OceanBaseDbType type)
        {
            var parameter = new OceanBaseParameter { OceanBaseDbType = type, Value = DBNull.Value };
            Assert.Equal(DbType.String, parameter.DbType);
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
        }

        [Theory]
        [InlineData("utf8mb4", 207)]
        [InlineData("utf8", 208)]
        [InlineData("UTF16", 207)]
        [InlineData("utf16", 208)]
        public void NationalCharacterCodecUsesSessionEncodingInsteadOfColumnCollation(string name, byte type)
        {
            const string text = "国字é\U0001f600  \ufeff";
            var settings = new NationalCharacterSet(name, "gbk", "gbk");
            var encoding = name.Equals("utf16", StringComparison.OrdinalIgnoreCase) ? NationalCharacterSet.Utf16 : ResultColumn.Utf8;
            var bytes = encoding.GetBytes(text);
            var column = ResultColumn.Parse(SchemaColumn("n", type, 32, charset: 28), OceanBaseCompatibilityMode.Oracle, nationalCharacterSet: settings);
            Assert.Equal(text, column.Decode(bytes));
            Assert.Equal(text, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(bytes)))));
            var parameter = new OceanBaseParameter { OceanBaseDbType = type == 207 ? OceanBaseDbType.NVarchar2 : OceanBaseDbType.NChar, Value = text, Size = text.Length };
            var encoded = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, settings);
            Assert.Equal(new byte[] { 0, 1, type, 0 }, encoded.AsSpan(0, 4).ToArray());
            Assert.Equal(WithLength(bytes), encoded.AsSpan(4).ToArray());
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.NotEmpty(PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, validateOnly: true));
            parameter.Size--;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, settings));
        }

        [Theory]
        [InlineData(null, "utf8mb4", "utf16", 54)]
        [InlineData("binary", "utf16", "utf8mb4", 45)]
        [InlineData("binary", "binary", "utf16", 54)]
        [InlineData(null, null, "utf8mb4", 45)]
        public void NationalCharacterFallbackUsesReportedResultAndConnectionSettings(string? national, string? results, string connection, ushort collation)
        {
            const string value = "中文\U0001f600";
            var settings = new NationalCharacterSet(national, results, connection);
            var resultEncoding = results == "utf16" || (results == null || results == "binary") && collation == 54 ? NationalCharacterSet.Utf16 : ResultColumn.Utf8;
            var column = ResultColumn.Parse(SchemaColumn("n", 207, 16, charset: collation), OceanBaseCompatibilityMode.Oracle, nationalCharacterSet: settings);
            Assert.Equal(value, column.Decode(resultEncoding.GetBytes(value)));
            var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.NChar, Value = value };
            var bytes = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, settings);
            var inputEncoding = connection == "utf16" ? NationalCharacterSet.Utf16 : ResultColumn.Utf8;
            Assert.Equal(WithLength(inputEncoding.GetBytes(value)), bytes.AsSpan(4).ToArray());
        }

        [Theory]
        [InlineData("utf8mb4")]
        [InlineData("utf16")]
        public void NationalCharacterCodecRejectsMalformedUnicodeAndMetadata(string encoding)
        {
            var settings = new NationalCharacterSet(encoding, "utf8mb4", "utf8mb4");
            var column = ResultColumn.Parse(SchemaColumn("n", 208, 8), OceanBaseCompatibilityMode.Oracle, nationalCharacterSet: settings);
            var invalid = encoding == "utf16" ? new byte[] { 216, 0 } : new byte[] { 0xc0, 0xaf };
            Assert.Throws<DecoderFallbackException>(() => column.Decode(invalid));
            Assert.Throws<DecoderFallbackException>(() => column.DecodeBinary(invalid));
            var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.NChar, Value = "\ud800" };
            Assert.Throws<EncoderFallbackException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, settings));
            Assert.Throws<EncoderFallbackException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, validateOnly: true));
            parameter.Value = 42;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, settings));
            parameter.Value = DBNull.Value; parameter.Scale = 1;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, settings));
            parameter.Scale = 0; parameter.Precision = 1;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, settings));
            Assert.Throws<NotSupportedException>(() => ResultColumn.Parse(SchemaColumn("n", 208, 8), OceanBaseCompatibilityMode.MySql).Decode(null));
            Assert.Throws<NotSupportedException>(() => ResultColumn.Parse(SchemaColumn("n", 207, 8), OceanBaseCompatibilityMode.Oracle).Decode(new byte[] { 65 }));
            Assert.Throws<NotSupportedException>(() => new NationalCharacterSet("unknown", "utf8mb4", "utf8mb4").ResultEncoding(45));
            Assert.Throws<NotSupportedException>(() => new NationalCharacterSet("binary", "binary", "utf8mb4").ResultEncoding(63));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NationalCharacterParameterValidationSendsNothingAndLeavesSessionUsable(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "reuse"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select :n";
                var parameter = new OceanBaseParameter { ParameterName = "n", OceanBaseDbType = OceanBaseDbType.NChar };
                command.Parameters.Add(parameter);
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                parameter.Value = "\ud800"; await Assert.ThrowsAsync<EncoderFallbackException>(execute);
                parameter.Value = 42; await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = "long"; parameter.Size = 1; await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = DBNull.Value; parameter.Precision = 1; await Assert.ThrowsAsync<NotSupportedException>(execute);
                parameter.Precision = 0; parameter.Scale = 1; await Assert.ThrowsAsync<NotSupportedException>(execute);
                Assert.Equal(ConnectionState.Open, connection.State);
                command.Parameters.Clear(); command.CommandText = "reuse"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task NationalCharacterSessionChangeRefreshesEncodingAndPreservesOldResultMetadata(bool asynchronous, bool deprecated)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "change national", deprecated);
                await channel.WritePacketAsync(SessionVariablesOk(new NationalCharacterSet("utf16", "utf8mb4", "utf8mb4").Variables("+00:00")));
                channel.BeginCommand(); await Query(channel, "national result", deprecated);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(SchemaColumn("n", 208, 16, charset: 45));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(WithLength(NationalCharacterSet.Utf16.GetBytes("国字\U0001f600  ")));
                await channel.WritePacketAsync(deprecated ? TimeZoneOk("+00:00", 254) : Eof());
                var request = await ReadNationalClientCommand(channel, "utf8mb4", deprecated);
                Assert.Equal("last", Encoding.UTF8.GetString(request, 1, request.Length - 1)); await channel.WritePacketAsync(Ok(1));
            }, deprecateEof: deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "change national";
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                Assert.Equal("utf16", connection.GetOpenSession().NationalCharacterSet!.National);
                Assert.Equal("utf8mb4", connection.GetOpenSession().Info!.SessionVariables["ncharacter_set_connection"]);
                command.CommandText = "national result";
                using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
                {
                    Assert.True(await reader.ReadAsync()); Assert.Equal("国字\U0001f600  ", reader.GetString(0));
                    Assert.False(await reader.ReadAsync()); Assert.Equal("NCHAR", reader.GetColumnSchema()[0].DataTypeName);
                }
                command.CommandText = "last"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("columns")]
        [InlineData("empty")]
        [InlineData("rows")]
        [InlineData("more")]
        [InlineData("name")]
        [InlineData("long")]
        [InlineData("connection")]
        [InlineData("type")]
        public async Task NationalCharacterProbeRejectsMalformedShapeBeforeBusinessSql(string fault)
        {
            var server = Server(async channel =>
            {
                var probe = await channel.ReadPacketAsync(); Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(probe, 1, probe.Length - 1));
                await TimeZoneReply(channel); channel.BeginCommand(); probe = await channel.ReadPacketAsync();
                Assert.Equal(NationalCharacterSetSql, Encoding.UTF8.GetString(probe, 1, probe.Length - 1));
                var count = fault == "columns" ? 2 : 3;
                await channel.WritePacketAsync(new byte[] { (byte)count });
                for (var i = 0; i < count; i++) await channel.WritePacketAsync(Column("charset", fault == "type" ? (byte)9 : (byte)253));
                await channel.WritePacketAsync(Eof());
                var values = WithLength(Encoding.UTF8.GetBytes(fault == "name" ? "bad name" : fault == "long" ? new string('a', 65) : "utf16"))
                    .Concat(WithLength(Encoding.UTF8.GetBytes("utf8mb4")))
                    .Concat(fault == "connection" ? new byte[] { 251 } : WithLength(Encoding.UTF8.GetBytes("utf8mb4"))).ToArray();
                if (fault != "empty") await channel.WritePacketAsync(count == 2 ? new byte[] { 1, 97, 1, 98 } : values);
                if (fault == "rows") await channel.WritePacketAsync(values);
                if (fault == "empty" || fault == "more") await channel.WritePacketAsync(Eof(fault == "more" ? (byte)14 : (byte)6));
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            }, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "business";
                var error = await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteNonQueryAsync());
                Assert.False(error is OceanBaseException database && database.IsExecutionOutcomeUnknown);
                Assert.Equal(fault == "empty" ? ConnectionState.Open : ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task NationalCharacterProbeUsesExistingCommandBudgetAndCancellation(bool asynchronous, bool cancel)
        {
            using var caller = new CancellationTokenSource(); Action? cancelCommand = null;
            var server = Server(async channel =>
            {
                var request = await channel.ReadPacketAsync(); Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await TimeZoneReply(channel); channel.BeginCommand(); request = await channel.ReadPacketAsync();
                Assert.Equal(NationalCharacterSetSql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                if (cancel) { if (asynchronous) caller.Cancel(); else cancelCommand!(); }
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            }, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "business"; command.CommandTimeout = 1; cancelCommand = command.Cancel;
                if (cancel)
                {
                    var error = asynchronous ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteNonQueryAsync(caller.Token))
                        : Assert.ThrowsAny<OperationCanceledException>(() => command.ExecuteNonQuery());
                    if (asynchronous) Assert.Equal(caller.Token, error.CancellationToken);
                }
                else if (asynchronous) await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteNonQueryAsync());
                else Assert.Throws<TimeoutException>(() => command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void NationalCharacterPrevalidationDoesNotApplyAnUnresolvedUtf8ByteBudget()
        {
            var value = new string('中', checked((int)(PacketChannel.DefaultMaxPacketSize / 3 + 1)));
            Assert.True(ResultColumn.Utf8.GetByteCount(value) > PacketChannel.DefaultMaxPacketSize);
            Assert.True(NationalCharacterSet.Utf16.GetByteCount(value) < PacketChannel.DefaultMaxPacketSize - 32);
            var parameter = new OceanBaseParameter { Value = value, OceanBaseDbType = OceanBaseDbType.NChar };
            Assert.NotEmpty(PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, validateOnly: true));
        }

        [Theory]
        [InlineData(false, 54)]
        [InlineData(true, 54)]
        [InlineData(false, 309)]
        [InlineData(true, 309)]
        [InlineData(false, 342)]
        [InlineData(true, 342)]
        public async Task NationalCharacterProbeReadsUnicodeNamesAndLeavesOrdinaryParametersUnchanged(bool asynchronous, ushort collation)
        {
            var encoding = collation == 309 ? ResultColumn.Utf8 : NationalCharacterSet.Utf16;
            var server = Server(async channel =>
            {
                var request = await channel.ReadPacketAsync(); Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await TimeZoneReply(channel); channel.BeginCommand(); request = await channel.ReadPacketAsync();
                Assert.Equal(NationalCharacterSetSql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(new byte[] { 3 });
                for (var i = 0; i < 3; i++) await channel.WritePacketAsync(Column("charset", 253, charset: collation));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new[] { "utf16", "utf16", "utf8mb4" }.SelectMany(s => WithLength(encoding.GetBytes(s))).ToArray());
                await channel.WritePacketAsync(Eof());
                request = await ReadNationalClientCommand(channel, "utf16"); Assert.Equal(22, request[0]); await Prepared(channel, 136, 2);
                request = await ReadNationalClientCommand(channel, "utf16");
                Assert.Equal(new byte[] { 0, 1, 208, 0, 253, 0 }.Concat(WithLength(NationalCharacterSet.Utf16.GetBytes("国字"))).Concat(WithLength(ResultColumn.Utf8.GetBytes("国字"))), request.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await ReadNationalClientCommand(channel, "utf16");
            }, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select :n, :ordinary";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = "国字", OceanBaseDbType = OceanBaseDbType.NChar });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "ordinary", Value = "国字" });
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NationalCharacterResultsRequireFreshFactsAfterUnreportedResults(bool reported)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "multiple");
                for (var i = 0; i < 2; i++)
                {
                    await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(SchemaColumn("n", 207, 16, charset: 54));
                    var bytes = i == 0 ? ResultColumn.Utf8.GetBytes("国字") : NationalCharacterSet.Utf16.GetBytes("国字");
                    await channel.WritePacketAsync(WithLength(bytes));
                    var end = reported && i == 0
                        ? SessionVariablesOk(new NationalCharacterSet("utf16", "utf8mb4", "utf8mb4").Variables("+00:00"), 254, 14)
                        : TimeZoneOk(null, 254, i == 0 ? (byte)14 : (byte)6);
                    await channel.WritePacketAsync(end);
                }
            }, deprecateEof: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "multiple";
                using var reader = await command.ExecuteReaderAsync(); var schema = reader.GetColumnSchema();
                Assert.True(await reader.ReadAsync()); Assert.Equal("国字", reader.GetString(0));
                Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
                if (reported) Assert.Equal("国字", reader.GetString(0));
                else Assert.Throws<NotSupportedException>(() => reader.GetString(0));
                Assert.Equal("NVARCHAR2", schema[0].DataTypeName);
                if (reported) Assert.False(await reader.ReadAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NationalCharacterProbeConflictClearsFactsBeforeReuse(bool timeZone)
        {
            var server = Server(async channel =>
            {
                var request = await channel.ReadPacketAsync(); Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await TimeZoneReply(channel, deprecated: true); channel.BeginCommand(); request = await channel.ReadPacketAsync();
                Assert.Equal(NationalCharacterSetSql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(new byte[] { 3 });
                for (var i = 0; i < 3; i++) await channel.WritePacketAsync(Column("charset", 253));
                await channel.WritePacketAsync(new[] { "utf16", "utf8mb4", "utf8mb4" }.SelectMany(s => WithLength(ResultColumn.Utf8.GetBytes(s))).ToArray());
                await channel.WritePacketAsync(SessionVariablesOk(new NationalCharacterSet(timeZone ? "utf16" : "utf8mb4", "utf8mb4", "utf8mb4").Variables(timeZone ? "+09:00" : "+00:00"), 254));
                request = await ReadNationalClientCommand(channel, "utf8mb4", deprecated: true);
                Assert.Equal("reuse", Encoding.UTF8.GetString(request, 1, request.Length - 1)); await channel.WritePacketAsync(Ok(1));
            }, deprecateEof: true, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "business";
                await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteNonQueryAsync());
                Assert.Null(connection.GetOpenSession().NationalCharacterSet);
                if (timeZone) Assert.Null(connection.GetOpenSession().SessionTimeZone);
                command.CommandText = "reuse"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
