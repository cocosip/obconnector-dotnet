using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class MySqlTemporalFallbackLiteralTests
    {
        public static IEnumerable<object[]> Values()
        {
            yield return new object[] { DbType.Date, new DateTime(2026, 10, 29), (byte)0, "'2026-10-29'" };
            yield return new object[] { DbType.Date, new OceanBaseDate(0, 0, 0), (byte)0, "'0000-00-00'" };
            yield return new object[] { DbType.Date, new OceanBaseDate(2026, 0, 29), (byte)0, "'2026-00-29'" };
            yield return new object[] { DbType.DateTime, new DateTime(2026, 10, 29, 12, 34, 56), (byte)0, "'2026-10-29 12:34:56'" };
            yield return new object[] { DbType.DateTime2, new DateTime(2026, 10, 29, 12, 34, 56, 123), (byte)3, "'2026-10-29 12:34:56.123'" };
            yield return new object[] { DbType.DateTime, new DateTime(2026, 10, 29, 12, 34, 56).AddTicks(1234560), (byte)6, "'2026-10-29 12:34:56.123456'" };
            yield return new object[] { DbType.DateTime, new OceanBaseDateTime(new OceanBaseDate(0, 0, 0)), (byte)6, "'0000-00-00 00:00:00'" };
            yield return new object[] { DbType.Time, TimeSpan.Zero, (byte)6, "'00:00:00'" };
            yield return new object[] { DbType.Time, TimeSpan.FromHours(27).Add(TimeSpan.FromMinutes(8)).Add(TimeSpan.FromSeconds(9)).Add(TimeSpan.FromTicks(4567890)), (byte)6, "'27:08:09.456789'" };
            yield return new object[] { DbType.Time, -(TimeSpan.FromHours(27).Add(TimeSpan.FromMinutes(8)).Add(TimeSpan.FromSeconds(9)).Add(TimeSpan.FromTicks(4567890))), (byte)6, "'-27:08:09.456789'" };
            yield return new object[] { DbType.Time, TimeSpan.FromHours(838).Add(TimeSpan.FromMinutes(59)).Add(TimeSpan.FromSeconds(59)).Add(TimeSpan.FromTicks(9999990)), (byte)6, "'838:59:59.999999'" };
            yield return new object[] { DbType.Time, TimeSpan.FromTicks(-10), (byte)6, "'-00:00:00.000001'" };
        }

        [Theory]
        [MemberData(nameof(Values))]
        public void MySqlTemporalFallbackLiteralsKeepInvariantExactValuesAndNull(DbType type, object value, byte scale, string literal)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var parameters = new OceanBaseParameterCollection();
                parameters.Add(new OceanBaseParameter { DbType = type, Value = value, Scale = scale });
                parameters.Add(new OceanBaseParameter { DbType = type, Value = DBNull.Value, Scale = scale });
                Assert.Equal("SELECT (" + literal + "),(NULL)", ClientPreparedSql.Build(
                    ParameterBindingPlan.Create("SELECT ?,?", OceanBaseCompatibilityMode.MySql, parameters), OceanBaseCompatibilityMode.MySql));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Theory]
        [InlineData(OceanBaseDbType.Date)]
        [InlineData(OceanBaseDbType.DateTime)]
        [InlineData(OceanBaseDbType.Time)]
        public void MySqlTemporalFallbackExplicitProvidersKeepTypedNullAndOracleRestriction(OceanBaseDbType provider)
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = provider, Value = DBNull.Value });
            Assert.Equal("SELECT (NULL)", ClientPreparedSql.Build(ParameterBindingPlan.Create("SELECT ?", OceanBaseCompatibilityMode.MySql, parameters), OceanBaseCompatibilityMode.MySql));
            Assert.Throws<NotSupportedException>(() => ClientPreparedSql.Build(ParameterBindingPlan.Create("SELECT :p", OceanBaseCompatibilityMode.Oracle, parameters), OceanBaseCompatibilityMode.Oracle));
        }
    }

    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlTemporalFallbackSnapshotsAndRepeatsPrepareWithoutStringDiscovery(bool asynchronous)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                for (var run = 0; run < 2; run++)
                {
                    await StatementCommand(channel, 22);
                    if (run == 0) { reached.SetResult(); await release.Task; }
                    await channel.WritePacketAsync(UnsupportedPrepare()); channel.BeginCommand();
                    await Query(channel, run == 0 ? "INSERT INTO t VALUES (('2026-10-29'),('2026-10-29 12:34:56.123456'),('-27:08:09.456789'))" : "INSERT INTO t VALUES ((NULL),(NULL),(NULL))");
                    await channel.WritePacketAsync(Ok(1, 2));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "INSERT INTO t VALUES (?,?,?)"; command.AllowClientSidePrepareFallback = true;
                var date = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Date, Value = new DateTime(2026, 10, 29) };
                var stamp = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.DateTime, Value = new DateTime(2026, 10, 29, 12, 34, 56).AddTicks(1234560), Scale = 6 };
                var duration = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Time, Value = -new TimeSpan(1, 3, 8, 9).Add(TimeSpan.FromTicks(4567890)), Scale = 6 };
                command.Parameters.Add(date); command.Parameters.Add(stamp); command.Parameters.Add(duration);
                var pending = Task.Run(async () => asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                await Task.WhenAny(reached.Task, pending).WaitAsync(TimeSpan.FromSeconds(5)); if (pending.IsFaulted) await pending;
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                date.Value = DateTime.UtcNow; stamp.Scale = 0; duration.Value = TimeSpan.MaxValue;
                release.SetResult(); Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                date.Value = stamp.Value = duration.Value = DBNull.Value; stamp.Scale = 6;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlTemporalFallbackInvalidInputsNeverSendPrepare(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel => { await Query(channel, "after invalid temporal fallback"); await channel.WritePacketAsync(Ok(1, 2)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "INSERT INTO t VALUES (?)"; command.AllowClientSidePrepareFallback = true;
                foreach (var parameter in new[]
                {
                    new OceanBaseParameter { DbType = DbType.Date, Value = new DateTime(2026, 10, 29, 1, 0, 0) },
                    new OceanBaseParameter { DbType = DbType.DateTime, Value = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc) },
                    new OceanBaseParameter { DbType = DbType.DateTime, Value = new DateTime(2026, 10, 29).AddTicks(1) },
                    new OceanBaseParameter { DbType = DbType.DateTime2, Value = new DateTime(2026, 10, 29).AddTicks(10), Scale = 0 },
                    new OceanBaseParameter { DbType = DbType.Time, Value = TimeSpan.FromHours(839) },
                    new OceanBaseParameter { DbType = DbType.Time, Value = TimeSpan.FromTicks(1) },
                    new OceanBaseParameter { DbType = DbType.Time, Value = DBNull.Value, Scale = 7 },
                    new OceanBaseParameter { DbType = DbType.Date, Value = DBNull.Value, Scale = 1 }
                })
                {
                    command.Parameters.Clear(); command.Parameters.Add(parameter);
                    var error = await Record.ExceptionAsync(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.True(error is ArgumentException || error is NotSupportedException);
                    Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Open, connection.State);
                }
                command.Parameters.Clear(); command.CommandText = "after invalid temporal fallback"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
