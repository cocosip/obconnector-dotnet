using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static ResultColumn IntervalColumn(bool daySecond, byte? scale = null, OceanBaseCompatibilityMode mode = OceanBaseCompatibilityMode.Oracle)
            => ResultColumn.Parse(SchemaColumn("i", daySecond ? (byte)205 : (byte)204, 32, scale: scale ?? (daySecond ? (byte)99 : (byte)9)), mode);

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void IntervalBothProtocolsKeepExactSignedComponentsAndProviderIdentity(bool daySecond, bool negative)
        {
            var payload = daySecond ? new byte[] { 0, 255, 201, 154, 59, 23, 59, 59, 255, 201, 154, 59, 9, 9 }
                : new byte[] { 0, 255, 201, 154, 59, 11, 9 };
            payload[0] = negative ? (byte)1 : (byte)0;
            var expected = (negative ? "-" : "+") + (daySecond ? "999999999 23:59:59.999999999" : "999999999-11");
            var column = IntervalColumn(daySecond);
            var name = "OceanBaseInterval" + (daySecond ? "DayToSecond" : "YearToMonth");
            Assert.Equal(name, column.FieldType.Name); Assert.Equal(column.FieldType, column.ProviderSpecificFieldType);
            Assert.Equal(daySecond ? OceanBaseDbType.IntervalDayToSecond : OceanBaseDbType.IntervalYearToMonth, column.ProviderType);
            Assert.Equal(daySecond ? "INTERVAL DAY TO SECOND" : "INTERVAL YEAR TO MONTH", column.DataTypeName);
            Assert.Equal(expected, column.Decode(payload).ToString());
            var cursor = new ByteCursor(new[] { (byte)payload.Length }.Concat(payload).Concat(new byte[] { 77 }).ToArray());
            var value = column.DecodeBinary(column.ReadBinaryField(cursor));
            Assert.Equal(expected, value.ToString()); Assert.Equal(77, cursor.ReadUInt8());
            var schema = new ResultDbColumn(column, 0);
            Assert.Equal(column.FieldType, schema.DataType); Assert.Equal(column.FieldType, schema["ProviderSpecificDataType"]);
            Assert.Equal(daySecond ? 6 : 5, schema["ProviderType"]); Assert.Null(schema.NumericPrecision); Assert.Null(schema.NumericScale);
            var parameter = new OceanBaseParameter { OceanBaseDbType = column.ProviderType, Value = value };
            Assert.Equal(DbType.Object, parameter.DbType);
            Assert.Equal(new byte[] { 0, 1, daySecond ? (byte)205 : (byte)204, 0, (byte)payload.Length }.Concat(payload), PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void IntervalTypedNullsRequireOracleAndValidPrecision(bool daySecond)
        {
            var column = IntervalColumn(daySecond);
            Assert.Equal(DBNull.Value, column.Decode(null)); Assert.Equal(DBNull.Value, column.DecodeBinary(null));
            var invalid = IntervalColumn(daySecond, mode: OceanBaseCompatibilityMode.MySql);
            Assert.Throws<NotSupportedException>(() => invalid.Decode(null));
            Assert.Throws<NotSupportedException>(() => invalid.DecodeBinary(null));
            var parameter = new OceanBaseParameter { OceanBaseDbType = daySecond ? OceanBaseDbType.IntervalDayToSecond : OceanBaseDbType.IntervalYearToMonth, Value = DBNull.Value };
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.Precision = 10;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Precision = 0; parameter.Scale = daySecond ? (byte)10 : (byte)1;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void IntervalAllPrecisionsRoundTripWithoutRounding(byte precision)
        {
            var leading = (int)Math.Pow(10, precision) - 1;
            var ym = new OceanBaseIntervalYearToMonth(true, leading, 11, precision);
            var ymParameter = new OceanBaseParameter { Value = ym };
            Assert.Equal(DbType.Object, ymParameter.DbType);
            var ymWire = PreparedParameters.Encode(new[] { ymParameter }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(204, ymWire[2]); Assert.Equal(7, ymWire[4]);
            Assert.Equal(ym, IntervalColumn(false, precision).Decode(ymWire.Skip(5).ToArray()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseIntervalYearToMonth(false, leading + 1, 0, precision));
            for (byte scale = 0; scale <= 9; scale++)
            {
                var nanos = 1000000000 - (int)Math.Pow(10, 9 - scale);
                var ds = new OceanBaseIntervalDayToSecond(true, leading, 23, 59, 59, nanos, precision, scale);
                var parameter = new OceanBaseParameter { Value = ds };
                var wire = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
                Assert.Equal(205, wire[2]); Assert.Equal(14, wire[4]);
                var column = IntervalColumn(true, (byte)(precision * 10 + scale));
                Assert.Equal(ds, column.Decode(wire.Skip(5).ToArray())); Assert.Equal(ds, column.DecodeBinary(wire.Skip(5).ToArray()));
                Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseIntervalDayToSecond(false, leading + 1, 0, 0, 0, 0, precision, scale));
                if (scale < 9) Assert.Throws<ArgumentException>(() => new OceanBaseIntervalDayToSecond(false, 0, 0, 0, 0, nanos + 1, precision, scale));
            }
        }

        [Fact]
        public void IntervalPublicValuesValidateComponentsAndConvertTimeSpanExactly()
        {
            Assert.Equal("+0-00", default(OceanBaseIntervalYearToMonth).ToString());
            Assert.Equal("+0 00:00:00", default(OceanBaseIntervalDayToSecond).ToString());
            Assert.Equal(TimeSpan.Zero, default(OceanBaseIntervalDayToSecond).ToTimeSpan());
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseIntervalYearToMonth(false, -1, 0));
            foreach (var month in new[] { -1, 12 }) Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseIntervalYearToMonth(false, 0, month));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseIntervalYearToMonth(false, 0, 0, 10));
            foreach (var args in new[] { new[] { -1, 0, 0, 0, 0 }, new[] { 0, -1, 0, 0, 0 }, new[] { 0, 24, 0, 0, 0 },
                new[] { 0, 0, -1, 0, 0 }, new[] { 0, 0, 60, 0, 0 }, new[] { 0, 0, 0, -1, 0 }, new[] { 0, 0, 0, 60, 0 },
                new[] { 0, 0, 0, 0, -1 }, new[] { 0, 0, 0, 0, 1000000000 } })
                Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseIntervalDayToSecond(false, args[0], args[1], args[2], args[3], args[4]));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseIntervalDayToSecond(false, 0, 0, 0, 0, leadingPrecision: 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseIntervalDayToSecond(false, 0, 0, 0, 0, scale: 10));
            Assert.Equal(TimeSpan.MaxValue, new OceanBaseIntervalDayToSecond(false, 10675199, 2, 48, 5, 477580700).ToTimeSpan());
            Assert.Equal(TimeSpan.MinValue, new OceanBaseIntervalDayToSecond(true, 10675199, 2, 48, 5, 477580800).ToTimeSpan());
            Assert.Throws<OverflowException>(() => new OceanBaseIntervalDayToSecond(false, 10675199, 2, 48, 5, 477580800).ToTimeSpan());
            Assert.Throws<OverflowException>(() => new OceanBaseIntervalDayToSecond(true, 10675199, 2, 48, 5, 477580900).ToTimeSpan());
            Assert.Throws<OverflowException>(() => new OceanBaseIntervalDayToSecond(false, 999999999, 0, 0, 0).ToTimeSpan());
            Assert.Throws<OverflowException>(() => new OceanBaseIntervalDayToSecond(false, 0, 0, 0, 0, 1).ToTimeSpan());
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                Assert.Equal("-0012-07", new OceanBaseIntervalYearToMonth(true, 12, 7, 4).ToString());
                Assert.Equal("-0012 03:04:05.123400", new OceanBaseIntervalDayToSecond(true, 12, 3, 4, 5, 123400000, 4, 6).ToString());
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void IntervalMalformedWireNeverDiscardsComponentsOrPrecision(bool daySecond)
        {
            var payload = daySecond ? new byte[] { 0, 12, 0, 0, 0, 3, 4, 5, 21, 205, 91, 7, 2, 9 }
                : new byte[] { 0, 12, 0, 0, 0, 7, 2 };
            var column = IntervalColumn(daySecond, daySecond ? (byte)29 : (byte)2);
            var corrupt = new[] { Array.Empty<byte>(), payload.Take(payload.Length - 1).ToArray(), payload.Concat(new byte[] { 0 }).ToArray() }.ToList();
            foreach (var mutation in daySecond ? new[] { (0, 2), (4, 128), (5, 24), (6, 60), (7, 60), (11, 255), (12, 1), (13, 8) }
                : new[] { (0, 2), (4, 128), (5, 12), (6, 1), (6, 10) })
            {
                var bytes = (byte[])payload.Clone(); bytes[mutation.Item1] = (byte)mutation.Item2; corrupt.Add(bytes);
            }
            foreach (var bytes in corrupt)
            {
                Assert.Throws<ProtocolException>(() => column.Decode(bytes));
                Assert.Throws<ProtocolException>(() => column.DecodeBinary(bytes));
            }
            foreach (var scale in daySecond ? new byte[] { 100, 127, 255 } : new byte[] { 10, 99, 255 })
            {
                var invalid = IntervalColumn(daySecond, scale);
                Assert.Throws<NotSupportedException>(() => invalid.Decode(null));
                Assert.Throws<NotSupportedException>(() => new ResultDbColumn(invalid, 0));
            }
        }

        [Fact]
        public void IntervalParametersKeepExplicitPrecisionAndRejectImplicitConversions()
        {
            var ym = new OceanBaseIntervalYearToMonth(true, 123, 7);
            var ds = new OceanBaseIntervalDayToSecond(false, 123, 3, 4, 5, 123456700);
            foreach (var pair in new[] { (OceanBaseDbType.IntervalYearToMonth, (object)ym), (OceanBaseDbType.IntervalDayToSecond, (object)ds) })
            {
                var parameter = new OceanBaseParameter { OceanBaseDbType = pair.Item1, Value = pair.Item2, Precision = 3 };
                Assert.Equal(3, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle)[pair.Item1 == OceanBaseDbType.IntervalYearToMonth ? 11 : 17]);
                parameter.Precision = 2; Assert.Throws<ArgumentOutOfRangeException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                parameter.Precision = 0;
                foreach (var invalid in new object[] { "123-7", TimeSpan.Zero, new byte[14], pair.Item1 == OceanBaseDbType.IntervalYearToMonth ? (object)ds : ym })
                {
                    parameter.Value = invalid; Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                }
                parameter.Value = pair.Item2; parameter.ResetDbType(); Assert.Null(parameter.OceanBaseDbType); Assert.Equal(DbType.Object, parameter.DbType);
                Assert.Equal(pair.Item1 == OceanBaseDbType.IntervalYearToMonth ? 204 : 205, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle)[2]);
                parameter.DbType = DbType.String;
                Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            }
            var fraction = new OceanBaseParameter { Value = ds, Scale = 7 };
            Assert.Equal(7, PreparedParameters.Encode(new[] { fraction }, OceanBaseCompatibilityMode.Oracle)[18]);
            fraction.Scale = 6; Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { fraction }, OceanBaseCompatibilityMode.Oracle));
            fraction.Scale = 0; Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { fraction }, OceanBaseCompatibilityMode.Oracle));
            fraction.Value = new OceanBaseIntervalDayToSecond(false, 1, 2, 3, 4);
            Assert.Equal(0, PreparedParameters.Encode(new[] { fraction }, OceanBaseCompatibilityMode.Oracle)[18]);
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
        public async Task IntervalPublicReaderWriteBackNullAndMultipleResults(bool asynchronous, bool binary, bool sequential)
        {
            var ymBytes = new byte[] { 1, 12, 0, 0, 0, 7, 2 };
            var dsBytes = new byte[] { 1, 12, 0, 0, 0, 3, 4, 5, 21, 205, 91, 7, 2, 9 };
            var ym = new OceanBaseIntervalYearToMonth(true, 12, 7, 2);
            var ds = new OceanBaseIntervalDayToSecond(true, 12, 3, 4, 5, 123456789, 2);
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 126, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "interval");
                await channel.WritePacketAsync(new byte[] { 3 });
                await channel.WritePacketAsync(SchemaColumn("ym", 204, 6, scale: 2));
                await channel.WritePacketAsync(SchemaColumn("ds", 205, 22, scale: 29));
                await channel.WritePacketAsync(SchemaColumn("tail", 253, 1)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync((binary ? new byte[] { 0, 0 } : Array.Empty<byte>()).Concat(WithLength(ymBytes)).Concat(WithLength(dsBytes)).Concat(new byte[] { 1, 120 }).ToArray());
                await channel.WritePacketAsync(binary ? new byte[] { 0, 12, 1, 120 } : new byte[] { 251, 251, 1, 120 });
                await channel.WritePacketAsync(Eof(14));
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(SchemaColumn("after", 253, 1)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(binary ? new byte[] { 0, 0, 1, 121 } : new byte[] { 1, 121 }); await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 127, 2);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 204, 0, 205, 0 }.Concat(WithLength(ymBytes)).Concat(WithLength(dsBytes)), execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1));
                var nullExecute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 3, 1, 6, 0, 6, 0 }, nullExecute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "reuse"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "interval"; if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    var schema = reader.GetColumnSchema(); var table = reader.GetSchemaTable()!;
                    Assert.Equal(typeof(OceanBaseIntervalYearToMonth), reader.GetFieldType(0)); Assert.Equal(typeof(OceanBaseIntervalDayToSecond), reader.GetProviderSpecificFieldType(1));
                    Assert.Equal(5, schema[0]["ProviderType"]); Assert.Equal(6, table.Rows[1]["ProviderType"]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(ym, asynchronous ? await reader.GetFieldValueAsync<OceanBaseIntervalYearToMonth>(0) : reader.GetFieldValue<OceanBaseIntervalYearToMonth>(0));
                    Assert.Equal(ym, reader.GetProviderSpecificValue(0));
                    Assert.Equal(ds, asynchronous ? await reader.GetFieldValueAsync<OceanBaseIntervalDayToSecond>(1) : reader.GetFieldValue<OceanBaseIntervalDayToSecond>(1));
                    Assert.Equal(ds, reader.GetProviderSpecificValue(1)); Assert.Equal("x", reader.GetString(2));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetValue(0)); Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(1)); Assert.Equal("x", reader.GetString(2));
                    Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.Equal("INTERVAL YEAR TO MONTH", schema[0].DataTypeName); Assert.Equal(typeof(OceanBaseIntervalDayToSecond), table.Rows[1]["DataType"]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("y", reader.GetString(0));
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select :ym, :ds";
                    var yp = new OceanBaseParameter { ParameterName = "ym", OceanBaseDbType = OceanBaseDbType.IntervalYearToMonth, Value = ym };
                    var dp = new OceanBaseParameter { ParameterName = "ds", OceanBaseDbType = OceanBaseDbType.IntervalDayToSecond, Value = ds };
                    command.Parameters.Add(yp); command.Parameters.Add(dp);
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    yp.Value = DBNull.Value; dp.Value = null;
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse"; Assert.Equal(1, await reuse.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task IntervalInvalidParametersRejectBeforePrepareAndValidParametersReuseSession(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 128, 1);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 204, 0, 7, 0, 12, 0, 0, 0, 7, 2 }, execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select :i";
                var parameter = new OceanBaseParameter { ParameterName = "i", OceanBaseDbType = OceanBaseDbType.IntervalYearToMonth, Value = "12-7" };
                command.Parameters.Add(parameter);
                Func<Task<int>> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = new OceanBaseIntervalYearToMonth(false, 12, 7, 2); parameter.Precision = 1;
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(execute);
                parameter.Value = DBNull.Value; parameter.Precision = 10;
                await Assert.ThrowsAsync<NotSupportedException>(execute);
                parameter.Precision = 2; parameter.Scale = 1;
                await Assert.ThrowsAsync<NotSupportedException>(execute);
                parameter.Scale = 0; parameter.Value = new OceanBaseIntervalYearToMonth(false, 12, 7, 2);
                Assert.Equal(1, await execute());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
