using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static void AddTypes(List<AcceptanceCase> cases)
    {
        void Add(string name, string mysql, string oracle, object value, OceanBaseDbType type,
            Func<object?, bool> matches, string? mode = null)
            => cases.Add(new("types." + name, "types", (c, t) => RoundTrip(c, c.Oracle ? oracle : mysql, value, type, matches, t), Mode: mode, RequiresObjects: true));
        Add("int64", "BIGINT", "NUMBER(19)", -8123456789012345678L, OceanBaseDbType.Int64,
            v => Number(v) == -8123456789012345678m);
        Add("decimal", "DECIMAL(24,6)", "NUMBER(24,6)", 123456789012345678.123456m, OceanBaseDbType.Decimal,
            v => Number(v) == 123456789012345678.123456m);
        var wide = new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345678", CultureInfo.InvariantCulture), 8);
        Add("decimal38", "DECIMAL(38,8)", "NUMBER(38,8)", wide, OceanBaseDbType.Decimal,
            v => v is OceanBaseDecimal n && n.Coefficient == wide.Coefficient && n.Scale == wide.Scale);
        Add("double", "DOUBLE", "BINARY_DOUBLE", 12345.125d, OceanBaseDbType.Double,
            v => Convert.ToDouble(v, CultureInfo.InvariantCulture) == 12345.125d);
        Add("unicode", "VARCHAR(100)", "VARCHAR2(100)", "中文🙂'quote", OceanBaseDbType.String, v => v is string s && s == "中文🙂'quote");
        Add("binary", "VARBINARY(100)", "RAW(100)", new byte[] { 0, 1, 127, 128, 255 }, OceanBaseDbType.Binary,
            v => v is byte[] b && b.SequenceEqual(new byte[] { 0, 1, 127, 128, 255 }));
        Add("null", "INTEGER", "NUMBER(9)", DBNull.Value, OceanBaseDbType.Int32, v => v is DBNull);
        var date = new DateTime(2026, 10, 20, 12, 34, 56, DateTimeKind.Unspecified);
        cases.Add(new("types.datetime", "types", (c, t) => RoundTrip(c, c.Oracle ? "DATE" : "DATETIME(6)", date,
            c.Oracle ? OceanBaseDbType.OracleDate : OceanBaseDbType.DateTime,
            v => v is OceanBaseDateTime d ? d.ToDateTime() == date : v is DateTime dt && dt == date, t), RequiresObjects: true));
        Add("unsigned64", "BIGINT UNSIGNED", "", ulong.MaxValue, OceanBaseDbType.UInt64,
            v => Convert.ToUInt64(v, CultureInfo.InvariantCulture) == ulong.MaxValue, "MySql");
        Add("year", "YEAR", "", (ushort)2026, OceanBaseDbType.Year, v => Convert.ToUInt16(v, CultureInfo.InvariantCulture) == 2026, "MySql");
        Add("bit64", "BIT(64)", "", 0x8000000000000001UL, OceanBaseDbType.Bit,
            v => v is ulong n && n == 0x8000000000000001UL, "MySql");
        Add("json", "JSON", "", "{\"message\":\"中文\",\"n\":42,\"value\":null}", OceanBaseDbType.Json,
            v => JsonMatches(v), "MySql");
        Add("time", "TIME(6)", "", new TimeSpan(2, 3, 4, 5, 123), OceanBaseDbType.Time,
            v => v is TimeSpan time && time == new TimeSpan(2, 3, 4, 5, 123), "MySql");
        Add("national", "", "NVARCHAR2(100)", "中文🙂", OceanBaseDbType.NVarchar2, v => v is string s && s == "中文🙂", "Oracle");
        Add("nchar", "", "NCHAR(10)", "中文", OceanBaseDbType.NChar, v => v is string s && s == "中文".PadRight(10), "Oracle");
        var ym = new OceanBaseIntervalYearToMonth(true, 12, 3);
        Add("interval-year-month", "", "INTERVAL YEAR(9) TO MONTH", ym, OceanBaseDbType.IntervalYearToMonth,
            v => v is OceanBaseIntervalYearToMonth actual && actual.Equals(ym), "Oracle");
        var ds = new OceanBaseIntervalDayToSecond(true, 12, 3, 4, 5, 123456789);
        Add("interval-day-second", "", "INTERVAL DAY(9) TO SECOND(9)", ds, OceanBaseDbType.IntervalDayToSecond,
            v => v is OceanBaseIntervalDayToSecond actual && actual.Equals(ds), "Oracle");
        var timestamp = new OceanBaseTimestamp(date, 123456789);
        Add("timestamp-nanoseconds", "", "TIMESTAMP(9)", timestamp, OceanBaseDbType.Timestamp,
            v => v is OceanBaseTimestamp actual && actual.Equals(timestamp), "Oracle");
        var zoned = new OceanBaseTimestampWithTimeZone(timestamp, TimeSpan.FromMinutes(330));
        Add("timestamp-time-zone", "", "TIMESTAMP(9) WITH TIME ZONE", zoned, OceanBaseDbType.TimestampWithTimeZone,
            v => v is OceanBaseTimestampWithTimeZone actual && actual.Timestamp.Equals(timestamp) && actual.Offset == zoned.Offset, "Oracle");
        cases.Add(new("types.timestamp-local-time-zone", "types", LocalTimeZone, Mode: "Oracle", RequiresObjects: true));
        cases.Add(new("types.zero-date-policy", "types", ZeroDatePolicy, Mode: "MySql",
            RequiresObjects: true, RequiresZeroDateProfile: true));
    }

    private static decimal Number(object? value) => value is OceanBaseDecimal number ? number.ToDecimal() : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private static bool JsonMatches(object? value)
    {
        if (value is not string json) return false;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("message").GetString() == "中文" &&
            document.RootElement.GetProperty("n").GetInt32() == 42 && document.RootElement.GetProperty("value").ValueKind == JsonValueKind.Null;
    }
    private static async Task RoundTrip(CaseContext c, string column, object value, OceanBaseDbType type,
        Func<object?, bool> matches, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        await RoundTripOnConnection(c, connection, column, value, type, matches, token);
    }
    private static async Task RoundTripOnConnection(CaseContext c, OceanBaseConnection connection, string column,
        object value, OceanBaseDbType type, Func<object?, bool> matches, CancellationToken token)
    {
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + column, token);
        await using (var command = c.Command(connection, "INSERT INTO " + table + " VALUES (1, " + c.ParameterSql(":value") + ")"))
        {
            var parameter = CaseContext.Parameter(command, "value", value); parameter.OceanBaseDbType = type;
            c.Check(await command.ExecuteNonQueryAsync(token) == 1, "Typed insert must affect one row.");
        }
        foreach (var prepared in new[] { false, true })
        {
            await using var command = c.Command(connection, "SELECT value FROM " + table + " WHERE id=" + (prepared ? c.ParameterSql(":id") : "1"));
            if (prepared) { CaseContext.Parameter(command, "id", 1, DbType.Int32); await command.PrepareAsync(token); }
            await using var reader = await command.ExecuteReaderAsync(token);
            c.Check(await reader.ReadAsync(token), "Typed query must return a row.");
            c.Check(matches(reader.GetProviderSpecificValue(0)), "Typed value must round-trip exactly through text and prepared results.");
            c.Check(!await reader.ReadAsync(token) && !await reader.NextResultAsync(token), "Typed query must consume its complete result.");
        }
    }
    private static async Task LocalTimeZone(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        await c.ExecuteAsync(connection, "ALTER SESSION SET TIME_ZONE = '+00:00'", token);
        var timestamp = new OceanBaseTimestamp(new DateTime(2026, 10, 20, 12, 34, 56), 123456789);
        var value = new OceanBaseTimestampLocalTimeZone(timestamp, "+00:00");
        await RoundTripOnConnection(c, connection, "TIMESTAMP(9) WITH LOCAL TIME ZONE", value, OceanBaseDbType.TimestampLocalTimeZone,
            v => v is OceanBaseTimestampLocalTimeZone actual && actual.Timestamp.Equals(timestamp), token);
    }

    private static async Task BinaryStream(CaseContext c, bool async, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + (c.Oracle ? "BLOB" : "LONGBLOB"), token);
        var bytes = Enumerable.Range(0, c.Config.LargeValueBytes).Select(i => (byte)(i * 31)).ToArray();
        using var input = new MemoryStream(bytes, false);
        await using (var insert = c.Command(connection, "INSERT INTO " + table + " VALUES (1, " + c.ParameterSql(":value") + ")"))
        {
            CaseContext.Parameter(insert, "value", input, DbType.Binary);
            c.Check((async ? await insert.ExecuteNonQueryAsync(token) : insert.ExecuteNonQuery()) == 1, "Stream insert must affect one row.");
        }
        c.Check(input.CanRead && input.Position == input.Length, "Driver must consume but retain caller stream ownership.");
        OceanBaseLob? lob = null;
        byte[]? actual = null;
        await using (var select = c.Command(connection, "SELECT value FROM " + table + " WHERE id=1"))
        await using (var reader = async ? await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token) : select.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            c.Check(async ? await reader.ReadAsync(token) : reader.Read(), "Stream query must return a row.");
            if (c.Oracle && reader.GetFieldType(0) == typeof(OceanBaseLob)) lob = (OceanBaseLob)reader.GetValue(0);
            else
            {
                using var source = reader.GetStream(0); using var target = new MemoryStream();
                if (async) await source.CopyToAsync(target, token); else source.CopyTo(target);
                actual = target.ToArray();
            }
            c.Check(!(async ? await reader.ReadAsync(token) : reader.Read()) && !(async ? await reader.NextResultAsync(token) : reader.NextResult()), "Stream query must finish before opening a locator stream.");
        }
        if (lob != null)
        {
            using (lob)
            using (var source = lob.OpenRead())
            using (var target = new MemoryStream())
            {
                if (async) await source.CopyToAsync(target, token); else source.CopyTo(target);
                actual = target.ToArray();
            }
        }
        c.Check(actual != null && actual.SequenceEqual(bytes), "Uploaded binary stream must match the stored bytes.");
    }
}
