using System;
using System.Data;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

/// <summary>
/// Every provider value type round-trips exactly through both the text and
/// the prepared read path, asserting GetProviderSpecificValue equality.
/// </summary>
public sealed class TypeRoundTripTests
{
    private sealed record TypeCase(string Key, string MySqlColumn, string OracleColumn, object Value,
        OceanBaseDbType Type, Func<object?, bool> Matches, string? Mode = null, string? SessionTimeZone = null);

    private static readonly DateTime Sample = new(2026, 10, 20, 12, 34, 56, DateTimeKind.Unspecified);

    private static readonly TypeCase[] Cases =
    {
        new("int64", "BIGINT", "NUMBER(19)", -8123456789012345678L, OceanBaseDbType.Int64,
            v => Number(v) == -8123456789012345678m),
        new("decimal", "DECIMAL(24,6)", "NUMBER(24,6)", 123456789012345678.123456m, OceanBaseDbType.Decimal,
            v => Number(v) == 123456789012345678.123456m),
        new("decimal38", "DECIMAL(38,8)", "NUMBER(38,8)",
            new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345678", CultureInfo.InvariantCulture), 8),
            OceanBaseDbType.Decimal,
            v => v is OceanBaseDecimal n && n.Coefficient == BigInteger.Parse("12345678901234567890123456789012345678", CultureInfo.InvariantCulture) && n.Scale == 8),
        new("double", "DOUBLE", "BINARY_DOUBLE", 12345.125d, OceanBaseDbType.Double,
            v => Convert.ToDouble(v, CultureInfo.InvariantCulture) == 12345.125d),
        new("unicode", "VARCHAR(100)", "VARCHAR2(100)", "中文🙂'quote", OceanBaseDbType.String,
            v => v is string s && s == "中文🙂'quote"),
        new("binary", "VARBINARY(100)", "RAW(100)", new byte[] { 0, 1, 127, 128, 255 }, OceanBaseDbType.Binary,
            v => v is byte[] b && b.AsSpan().SequenceEqual(new byte[] { 0, 1, 127, 128, 255 })),
        new("null", "INTEGER", "NUMBER(9)", DBNull.Value, OceanBaseDbType.Int32, v => v is DBNull),
        new("datetime", "DATETIME(6)", "DATE", Sample, OceanBaseDbType.DateTime,
            v => v is OceanBaseDateTime d ? d.ToDateTime() == Sample : v is DateTime dt && dt == Sample),
        new("unsigned64", "BIGINT UNSIGNED", "", ulong.MaxValue, OceanBaseDbType.UInt64,
            v => Convert.ToUInt64(v, CultureInfo.InvariantCulture) == ulong.MaxValue, "MySql"),
        new("year", "YEAR", "", (ushort)2026, OceanBaseDbType.Year,
            v => Convert.ToUInt16(v, CultureInfo.InvariantCulture) == 2026, "MySql"),
        new("bit64", "BIT(64)", "", 0x8000000000000001UL, OceanBaseDbType.Bit,
            v => v is ulong n && n == 0x8000000000000001UL, "MySql"),
        new("json", "JSON", "", "{\"message\":\"中文\",\"n\":42,\"value\":null}", OceanBaseDbType.Json,
            v => JsonMatches(v), "MySql"),
        new("time", "TIME(6)", "", new TimeSpan(2, 3, 4, 5, 123), OceanBaseDbType.Time,
            v => v is TimeSpan time && time == new TimeSpan(2, 3, 4, 5, 123), "MySql"),
        new("national", "", "NVARCHAR2(100)", "中文🙂", OceanBaseDbType.NVarchar2,
            v => v is string s && s == "中文🙂", "Oracle"),
        new("nchar", "", "NCHAR(10)", "中文", OceanBaseDbType.NChar,
            v => v is string s && s == "中文".PadRight(10), "Oracle"),
        new("interval-year-month", "", "INTERVAL YEAR(9) TO MONTH", new OceanBaseIntervalYearToMonth(true, 12, 3),
            OceanBaseDbType.IntervalYearToMonth, v => v is OceanBaseIntervalYearToMonth actual && actual.Equals(new OceanBaseIntervalYearToMonth(true, 12, 3)), "Oracle"),
        new("interval-day-second", "", "INTERVAL DAY(9) TO SECOND(9)", new OceanBaseIntervalDayToSecond(true, 12, 3, 4, 5, 123456789),
            OceanBaseDbType.IntervalDayToSecond, v => v is OceanBaseIntervalDayToSecond actual && actual.Equals(new OceanBaseIntervalDayToSecond(true, 12, 3, 4, 5, 123456789)), "Oracle"),
        new("timestamp-nanoseconds", "", "TIMESTAMP(9)", new OceanBaseTimestamp(Sample, 123456789),
            OceanBaseDbType.Timestamp, v => v is OceanBaseTimestamp actual && actual.Equals(new OceanBaseTimestamp(Sample, 123456789)), "Oracle"),
        new("timestamp-time-zone", "", "TIMESTAMP(9) WITH TIME ZONE",
            new OceanBaseTimestampWithTimeZone(new OceanBaseTimestamp(Sample, 123456789), TimeSpan.FromMinutes(330)),
            OceanBaseDbType.TimestampWithTimeZone,
            v => v is OceanBaseTimestampWithTimeZone actual && actual.Timestamp.Equals(new OceanBaseTimestamp(Sample, 123456789)) && actual.Offset == TimeSpan.FromMinutes(330), "Oracle"),
        new("timestamp-local-time-zone", "", "TIMESTAMP(9) WITH LOCAL TIME ZONE",
            new OceanBaseTimestampLocalTimeZone(new OceanBaseTimestamp(new DateTime(2026, 10, 20, 12, 34, 56), 123456789), "+00:00"),
            OceanBaseDbType.TimestampLocalTimeZone,
            v => v is OceanBaseTimestampLocalTimeZone actual && actual.Timestamp.Equals(new OceanBaseTimestamp(new DateTime(2026, 10, 20, 12, 34, 56), 123456789)), "Oracle", "+00:00"),
    };

    public static TheoryData<string, string> Rows()
    {
        var data = new TheoryData<string, string>();
        foreach (var profile in IntegrationEnvironment.Profiles)
        {
            if (!profile.CreateObjects) continue;
            foreach (var item in Cases)
                if (item.Mode is null || item.Mode == (profile.Oracle ? "Oracle" : "MySql"))
                    data.Add(profile.Name, item.Key);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task TypedValuesRoundTripThroughTextAndPreparedReaders(string profileName, string key)
    {
        var item = Array.Find(Cases, candidate => candidate.Key == key)!;
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        if (item.SessionTimeZone is not null)
            await db.ExecuteAsync(connection, "ALTER SESSION SET TIME_ZONE = '" + item.SessionTimeZone + "'", CancellationToken.None);
        var column = db.Oracle ? item.OracleColumn : item.MySqlColumn;
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + column, CancellationToken.None);
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1, " + db.ParameterSql(":value") + ")"))
        {
            var parameter = DatabaseContext.Parameter(insert, "value", item.Value);
            parameter.OceanBaseDbType = item.Type;
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
        foreach (var prepared in new[] { false, true })
        {
            await using var command = db.Command(connection, "SELECT value FROM " + table + " WHERE id=" + (prepared ? db.ParameterSql(":id") : "1"));
            if (prepared)
            {
                DatabaseContext.Parameter(command, "id", 1, DbType.Int32);
                await command.PrepareAsync();
            }
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Typed query must return a row.");
            Assert.True(item.Matches(reader.GetProviderSpecificValue(0)),
                $"Type '{item.Key}' must round-trip exactly through {(prepared ? "prepared" : "text")} results.");
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
        }
    }

    private static decimal Number(object? value) =>
        value is OceanBaseDecimal number ? number.ToDecimal() : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static bool JsonMatches(object? value)
    {
        if (value is not string json) return false;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("message").GetString() == "中文" &&
            document.RootElement.GetProperty("n").GetInt32() == 42 &&
            document.RootElement.GetProperty("value").ValueKind == JsonValueKind.Null;
    }
}
