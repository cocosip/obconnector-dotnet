using System;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private const string SessionTimeZoneSql = "SELECT SESSIONTIMEZONE FROM DUAL";

        private static async Task TimeZoneReply(PacketChannel channel, string zone = "+00:00", bool deprecated = false)
        {
            await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(Column("SESSIONTIMEZONE", 253));
            if (!deprecated) await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(WithLength(Encoding.UTF8.GetBytes(zone)));
            await channel.WritePacketAsync(deprecated ? TimeZoneOk(null, 254) : Eof());
        }

        internal static async Task<byte[]> ReadClientCommand(PacketChannel channel, bool deprecated = false)
        {
            var request = await channel.ReadPacketAsync();
            while (request[0] == 3)
            {
                var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                if (sql == SessionTimeZoneSql) await TimeZoneReply(channel, deprecated: deprecated);
                else if (sql == NationalCharacterSetSql) await NationalCharacterSetReply(channel, deprecated: deprecated);
                else break;
                channel.BeginCommand();
                request = await channel.ReadPacketAsync();
            }
            return request;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void LocalTimeZoneTimestampMetadataHasStableProviderIdentity(byte scale)
        {
            var column = ValueColumn(201, OceanBaseCompatibilityMode.Oracle, scale: scale);
            Assert.Equal("OceanBaseConnector.OceanBaseTimestampLocalTimeZone", column.FieldType.FullName);
            Assert.Equal(column.FieldType, column.ProviderSpecificFieldType);
            Assert.Equal("TIMESTAMP WITH LOCAL TIME ZONE", column.DataTypeName);
            Assert.Equal("TimestampLocalTimeZone", column.ProviderType.ToString());
            Assert.Equal(DBNull.Value, column.Decode(null)); Assert.Equal(DBNull.Value, column.DecodeBinary(null));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task LocalTimeZoneTimestampMissingReportIsQueriedBeforeBusinessSql(bool asynchronous, bool deprecated)
        {
            var server = Server(async channel =>
            {
                var query = await channel.ReadPacketAsync();
                Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(query, 1, query.Length - 1));
                Assert.Equal(3, query[0]);
                await TimeZoneReply(channel, "Asia/Tokyo", deprecated);
                channel.BeginCommand(); await Query(channel, "business", deprecated);
                await channel.WritePacketAsync(Ok(3));
            }, deprecated, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "business";
                Assert.Equal(3, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(0, 0U)]
        [InlineData(7, 123456700U)]
        [InlineData(8, 123456780U)]
        [InlineData(9, 123456789U)]
        public void LocalTimeZoneTimestampCodecAndParametersPreserveExactFields(byte scale, uint nanos)
        {
            var packet = ValueColumnPacket(201, scale: scale);
            var column = ResultColumn.Parse(packet, OceanBaseCompatibilityMode.Oracle, "America/New_York");
            var expected = new OceanBaseTimestampLocalTimeZone(new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), (int)nanos, scale), "America/New_York");
            var payload = NanosPayload(nanos, scale);
            Assert.Equal(expected, column.Decode(payload));
            Assert.Equal(expected, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(payload)))));
            Assert.Throws<NotSupportedException>(() => ValueColumn(201, OceanBaseCompatibilityMode.Oracle, scale: scale).Decode(payload));
            var parameter = new OceanBaseParameter { Value = expected };
            Assert.Equal(DbType.DateTime2, parameter.DbType);
            var encoded = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(new byte[] { 0, 1, 201, 0 }, encoded.AsSpan(0, 4).ToArray());
            Assert.Equal(WithLength(payload), encoded.AsSpan(4).ToArray());
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            PreparedParameters.ValidateSessionTimeZone(new[] { parameter }, "America/New_York");
            Assert.Throws<ArgumentException>(() => PreparedParameters.ValidateSessionTimeZone(new[] { parameter }, "-05:00"));
            parameter.OceanBaseDbType = OceanBaseDbType.TimestampLocalTimeZone;
            parameter.Value = DBNull.Value;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.Scale = 10;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData("")]
        [InlineData("SYSTEM")]
        [InlineData("system")]
        [InlineData("+14:01")]
        [InlineData("-15:00")]
        [InlineData("+5:30")]
        [InlineData("+01:60")]
        [InlineData("bad zone")]
        [InlineData("bad\nzone")]
        public void LocalTimeZoneTimestampRejectsUnresolvedOrMalformedIdentity(string zone)
            => Assert.Throws<ArgumentException>(() => new OceanBaseTimestampLocalTimeZone(default, zone));

        [Fact]
        public void LocalTimeZoneTimestampDoesNotResolveDstOrLoseNanoseconds()
        {
            var local = new OceanBaseTimestamp(new DateTime(2024, 11, 3, 1, 30, 0), 100);
            var value = new OceanBaseTimestampLocalTimeZone(local, "America/New_York");
            Assert.Equal(DateTimeKind.Unspecified, value.ToDateTime().Kind);
            Assert.Equal(local.ToDateTime(), value.ToDateTime());
            Assert.Equal("2024-11-03 01:30:00.000000100 [America/New_York]", value.ToString());
            Assert.Throws<OverflowException>(() => new OceanBaseTimestampLocalTimeZone(new OceanBaseTimestamp(local.WholeSeconds, 1), "+14:00").ToDateTime());
            Assert.Throws<ArgumentException>(() => new OceanBaseTimestampLocalTimeZone(default, new string('a', 256)));
            Assert.Throws<ArgumentException>(() => new OceanBaseTimestampLocalTimeZone(default, new string('中', 86)));
            Assert.Throws<EncoderFallbackException>(() => new OceanBaseTimestampLocalTimeZone(default, new string((char)0xd800, 1)));
            Assert.Throws<InvalidOperationException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = default(OceanBaseTimestampLocalTimeZone) } }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => ValueColumn(201, OceanBaseCompatibilityMode.MySql).Decode(null));
            Assert.Throws<NotSupportedException>(() => ValueColumn(201, OceanBaseCompatibilityMode.Oracle, scale: 10).Decode(null));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task LocalTimeZoneTimestampPublicReaderAndWriteBack(bool asynchronous, bool binary)
        {
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 107, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "values");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(ValueColumnPacket(201, scale: 9)); await channel.WritePacketAsync(Eof());
                foreach (var nanos in new[] { 123456789U, 100U })
                {
                    var field = WithLength(NanosPayload(nanos));
                    if (binary) { var row = new byte[field.Length + 2]; field.CopyTo(row, 2); field = row; }
                    await channel.WritePacketAsync(field);
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4 } : new byte[] { 251 });
                await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
                // The mismatch must fail before preparing; the following successful write is the first prepare.
                await StatementCommand(channel, 22); await Prepared(channel, 108, 1);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 201, 0 }, execute.AsSpan(10, 4).ToArray());
                Assert.Equal(WithLength(NanosPayload()), execute.AsSpan(14).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); OceanBaseTimestampLocalTimeZone first;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "values";
                    if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                    Assert.Equal(typeof(OceanBaseTimestampLocalTimeZone), reader.GetFieldType(0));
                    Assert.Equal(typeof(OceanBaseTimestampLocalTimeZone), Assert.Single(reader.GetColumnSchema()).DataType);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    first = reader.GetFieldValue<OceanBaseTimestampLocalTimeZone>(0);
                    Assert.Equal("+00:00", first.SessionTimeZone); Assert.Equal(123456789, first.Timestamp.Nanosecond);
                    Assert.Equal(first, reader.GetProviderSpecificValue(0));
                    Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
                    Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<DateTimeOffset>(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1), reader.GetFieldValue<DateTime>(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using var insert = connection.CreateCommand(); insert.CommandText = "select :t";
                var parameter = new OceanBaseParameter { ParameterName = "t", Value = new OceanBaseTimestampLocalTimeZone(first.Timestamp, "Asia/Tokyo") };
                insert.Parameters.Add(parameter);
                if (asynchronous) await Assert.ThrowsAsync<ArgumentException>(() => insert.ExecuteNonQueryAsync());
                else Assert.Throws<ArgumentException>(() => insert.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
                parameter.Value = first;
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "disconnect")]
        [InlineData(true, "disconnect")]
        [InlineData(false, "rows")]
        [InlineData(true, "rows")]
        [InlineData(false, "null")]
        [InlineData(true, "SYSTEM")]
        [InlineData(false, "utf8")]
        [InlineData(true, "length")]
        [InlineData(false, "binary")]
        [InlineData(true, "more")]
        public async Task LocalTimeZoneTimestampProbeFailureNeverSendsBusinessSql(bool asynchronous, string fault)
        {
            var server = Server(async channel =>
            {
                var query = await channel.ReadPacketAsync();
                Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(query, 1, query.Length - 1));
                if (fault == "disconnect") return;
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("zone", 253, charset: fault == "binary" ? (ushort)63 : (ushort)45));
                await channel.WritePacketAsync(Eof());
                var bytes = fault == "utf8" ? new byte[] { 255 } : Encoding.UTF8.GetBytes(fault == "length" ? new string('a', 256) : fault == "SYSTEM" ? "SYSTEM" : "+00:00");
                await channel.WritePacketAsync(fault == "null" ? new byte[] { 251 } : WithLength(bytes));
                if (fault == "rows") await channel.WritePacketAsync(WithLength(bytes));
                if (fault == "more") await channel.WritePacketAsync(Eof(14));
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            }, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "business";
                var error = asynchronous ? await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteNonQueryAsync())
                    : Assert.ThrowsAny<Exception>(() => command.ExecuteNonQuery());
                Assert.False(error is OceanBaseException database && database.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void LocalTimeZoneTimestampCannotOverrideExplicitUnzonedProviderType()
        {
            var parameter = new OceanBaseParameter
            {
                OceanBaseDbType = OceanBaseDbType.Timestamp,
                Value = new OceanBaseTimestampLocalTimeZone(default, "+00:00")
            };
            Assert.ThrowsAny<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LocalTimeZoneTimestampConflictingProbeReportIsNotTrusted(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                var initial = await channel.ReadPacketAsync();
                Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(initial, 1, initial.Length - 1));
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("zone", 253));
                await channel.WritePacketAsync(WithLength(Encoding.UTF8.GetBytes("Asia/Tokyo")));
                await channel.WritePacketAsync(TimeZoneOk("+00:00", 254));
                channel.BeginCommand();
                var next = await channel.ReadPacketAsync();
                Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(next, 1, next.Length - 1));
                await TimeZoneReply(channel, deprecated: true);
                channel.BeginCommand(); await Query(channel, "business", deprecated: true); await channel.WritePacketAsync(Ok(1));
            }, deprecateEof: true, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "business";
                if (asynchronous) await Assert.ThrowsAsync<ProtocolException>(() => command.ExecuteNonQueryAsync());
                else Assert.Throws<ProtocolException>(() => command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Null(connection.GetOpenSession().SessionTimeZone);
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task LocalTimeZoneTimestampProbeUsesCommandTimeoutAndCallerCancellation(bool asynchronous, bool cancel)
        {
            using var caller = new CancellationTokenSource();
            Action? cancelCommand = null;
            var server = Server(async channel =>
            {
                var probe = await channel.ReadPacketAsync();
                Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(probe, 1, probe.Length - 1));
                if (cancel) { if (asynchronous) caller.Cancel(); else cancelCommand!(); }
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            }, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "business"; command.CommandTimeout = 1; cancelCommand = command.Cancel;
                Exception error;
                if (cancel)
                {
                    var canceled = asynchronous ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteNonQueryAsync(caller.Token))
                        : Assert.ThrowsAny<OperationCanceledException>(() => command.ExecuteNonQuery());
                    if (asynchronous) Assert.Equal(caller.Token, canceled.CancellationToken); error = canceled;
                }
                else error = asynchronous ? await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteNonQueryAsync()) : Assert.Throws<TimeoutException>(() => command.ExecuteNonQuery());
                Assert.False(error is OceanBaseException database && database.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LocalTimeZoneTimestampProbeServerErrorKeepsUnknownStateAndRejectsBindingsFirst(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                var probe = await channel.ReadPacketAsync();
                Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(probe, 1, probe.Length - 1));
                await channel.WritePacketAsync(Error());
                channel.BeginCommand();
                probe = await channel.ReadPacketAsync();
                Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(probe, 1, probe.Length - 1));
                await TimeZoneReply(channel); channel.BeginCommand(); await Query(channel, "business"); await channel.WritePacketAsync(Ok(1));
            }, reportTimeZone: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select :t";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "t", Value = Guid.NewGuid() });
                if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync());
                else Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                command.Parameters.Clear(); command.CommandText = "business";
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync()) : Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Null(connection.GetOpenSession().SessionTimeZone);
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task LocalTimeZoneTimestampMultiResultsUseOnlyTheirOwnSnapshot(bool asynchronous, bool report)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "values");
                for (var i = 0; i < 3; i++)
                {
                    await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ValueColumnPacket(201, scale: 9));
                    await channel.WritePacketAsync(WithLength(NanosPayload()));
                    await channel.WritePacketAsync(TimeZoneOk(i == 0 && report ? "Asia/Tokyo" : null, 254, i < 2 ? (byte)14 : (byte)6));
                }
            }, deprecateEof: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "values";
                using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                for (var i = 0; i < 3; i++)
                {
                    if (i > 0) Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.Equal(typeof(OceanBaseTimestampLocalTimeZone), reader.GetFieldType(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    if (i == 0 || i == 1 && report)
                        Assert.Equal(i == 0 ? "+00:00" : "Asia/Tokyo", reader.GetFieldValue<OceanBaseTimestampLocalTimeZone>(0).SessionTimeZone);
                    else Assert.Throws<NotSupportedException>(() => reader.GetValue(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
