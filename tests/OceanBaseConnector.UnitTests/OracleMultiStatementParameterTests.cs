using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleNationalMultiStatementsUseUnicodeLiteralsAndKeepSessionContext(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                var request = await ReadNationalClientCommand(channel, "utf16");
                Assert.Equal(3, request[0]);
                Assert.Equal("insert into t values ((u'\\4e2d\\d83d\\de42\\0027\\005c\\0000')); update t set v=(NULL), n=(u'\\4e2d\\d83d\\de42\\0027\\005c\\0000')",
                    System.Text.Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(Ok(2, 14)); await channel.WritePacketAsync(Ok(3));
                channel.BeginCommand(); await Query(channel, "after national multi parameters"); await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true, nationalCharacterSet: "utf16");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "insert into t values (:n); update t set v=:v, n=:N";
                var parameter = new OceanBaseParameter { ParameterName = "n", OceanBaseDbType = OceanBaseDbType.NChar, Value = "中🙂'\\\0", Size = 6 };
                command.Parameters.Add(parameter);
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "v", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = DBNull.Value });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal("中🙂'\\\0", parameter.Value);
                command.Parameters.Clear(); command.CommandText = "after national multi parameters";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleNationalMultiStatementsDoNotUseBinaryEncodingForUnicodeLiterals(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "insert into t values ((u'\\4e2d')); select 1");
                await channel.WritePacketAsync(Ok(1, 14)); await channel.WritePacketAsync(Ok(0));
                channel.BeginCommand(); await Query(channel, "after national text input"); await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true, nationalCharacterSet: "unsupported");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "insert into t values (:n); select 1";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = "中" });
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                command.Parameters.Clear(); command.CommandText = "after national text input";
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
        public async Task OracleParameterizedMultiStatementsUseTextAndKeepAllResultSemantics(bool asynchronous, bool scalar)
        {
            var server = Server(async channel =>
            {
                for (var value = 7; value <= 8; value++)
                {
                    channel.BeginCommand();
                    await Query(channel, "select (" + value + "), ':hidden', q'[; :hidden]' from dual; select (" + value + ") from dual");
                    await channel.WritePacketAsync(new byte[] { 1 });
                    await channel.WritePacketAsync(Column("n"));
                    await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(new byte[] { 1, (byte)('0' + value) });
                    await channel.WritePacketAsync(Eof(14));
                    await channel.WritePacketAsync(new byte[] { 1 });
                    await channel.WritePacketAsync(Column("n"));
                    await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(new byte[] { 1, (byte)('0' + value) });
                    await channel.WritePacketAsync(Eof());
                }
                channel.BeginCommand();
                await Query(channel, "after oracle multi parameters");
                await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync();
                var session = connection.GetOpenSession();
                using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "select :P, ':hidden', q'[; :hidden]' from dual; select :p from dual";
                var parameter = new OceanBaseParameter { ParameterName = "p", Value = 7 };
                command.Parameters.Add(parameter);
                for (var value = 7; value <= 8; value++)
                {
                    parameter.Value = value;
                    if (scalar) Assert.Equal(value, asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar());
                    else
                    {
                        using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                        for (var result = 0; result < 2; result++)
                        {
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(value, reader.GetInt32(0));
                            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(result == 0, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                    }
                }
                command.Parameters.Clear(); command.CommandText = "after oracle multi parameters";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Same(session, connection.GetOpenSession());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleParameterizedMultiStatementsPreserveLiteralsAndUpdateCounts(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "insert into t values ((CAST('quote''\\中文; :p' AS VARCHAR2(4000))), (HEXTORAW('0027FF')), (NULL)); update t set n=(1.2500)");
                await channel.WritePacketAsync(Ok(2, 14));
                await channel.WritePacketAsync(Ok(3));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "insert into t values (:s, :b, :n); update t set n=:d";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "s", Value = "quote'\\中文; :p" });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "b", Value = new byte[] { 0, 39, 255 } });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = DBNull.Value });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "d", Value = 1.25m, Scale = 4 });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleTemporalMultiStatementsUseDateAndTimestampLiterals(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "select (TO_DATE('2026-01-02 03:04:05','YYYY-MM-DD HH24:MI:SS')) from dual; select (TIMESTAMP '2026-01-02 03:04:05.123456789') from dual");
                await channel.WritePacketAsync(Ok(2, 14)); await channel.WritePacketAsync(Ok(3));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "select :d from dual; select :t from dual";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "d", DbType = DbType.Date, Value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified) });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "t", OceanBaseDbType = OceanBaseDbType.Timestamp,
                    Value = new OceanBaseTimestamp(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), 123456789) });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleIntervalMultiStatementsUseTypedLiterals(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "insert into t values ((INTERVAL '-012-03' YEAR(3) TO MONTH)); update t set v=(INTERVAL '+004 05:06:07.123456789' DAY(3) TO SECOND(9))");
                await channel.WritePacketAsync(Ok(2, 14)); await channel.WritePacketAsync(Ok(3));
                channel.BeginCommand(); await Query(channel, "after interval multi parameters");
                await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "insert into t values (:ym); update t set v=:ds";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "ym", Precision = 3,
                    Value = new OceanBaseIntervalYearToMonth(true, 12, 3) });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "ds", OceanBaseDbType = OceanBaseDbType.IntervalDayToSecond,
                    Value = new OceanBaseIntervalDayToSecond(false, 4, 5, 6, 7, 123456789, 3) });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                command.Parameters.Clear(); command.CommandText = "after interval multi parameters";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleZonedMultiStatementsKeepSessionValidationAndTypedText(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "insert into t values ((TIMESTAMP '2026-01-02 03:04:05.123456789 -00:30')); update t set v=(CAST(FROM_TZ(TIMESTAMP '2026-01-02 03:04:05.123456789', '+00:00') AS TIMESTAMP(9) WITH LOCAL TIME ZONE))");
                await channel.WritePacketAsync(Ok(2, 14)); await channel.WritePacketAsync(Ok(3));
                channel.BeginCommand(); await Query(channel, "after zoned multi parameters"); await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "insert into t values (:z); update t set v=:l";
                var timestamp = new OceanBaseTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), 123456789);
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "z", Value = new OceanBaseTimestampWithTimeZone(timestamp, TimeSpan.FromMinutes(-30)) });
                var local = new OceanBaseParameter { ParameterName = "l", Value = new OceanBaseTimestampLocalTimeZone(timestamp, "+05:45") };
                command.Parameters.Add(local);
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<ArgumentException>(execute);
                Assert.Equal(ConnectionState.Open, connection.State);
                local.Value = new OceanBaseTimestampLocalTimeZone(timestamp, "+00:00");
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                command.Parameters.Clear(); command.CommandText = "after zoned multi parameters";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleParameterizedMultiStatementsRejectUnsupportedInputsBeforeSending(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "after rejected oracle multi parameters");
                await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "select :p from dual; select :p from dual";
                var parameter = new OceanBaseParameter { ParameterName = "p", Value = 7 };
                command.Parameters.Add(parameter);
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                command.FetchSize = 1; await Assert.ThrowsAsync<NotSupportedException>(execute); command.FetchSize = 0;
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.PrepareAsync() : Task.Run(command.Prepare));
                using var stream = new MemoryStream(new byte[] { 1 });
                parameter.Value = stream; parameter.DbType = DbType.Binary; await Assert.ThrowsAsync<NotSupportedException>(execute);
                Assert.Equal(0, stream.Position);
                parameter.Value = "secret\0value"; parameter.DbType = DbType.String; await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = new string('x', 4001); await Assert.ThrowsAsync<ArgumentException>(execute);
                command.Parameters.Clear(); command.CommandText = "after rejected oracle multi parameters";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PreviouslyPreparedCommandsCannotTurnIntoTextMultiStatements(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 42, 1);
                var request = await StatementCommand(channel, 23);
                Assert.Equal(42, request[1]);
                await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "select :p from dual";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = 7 });
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                command.CommandText += "; select :p from dual";
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                command.CommandText = "select :p from dual";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
