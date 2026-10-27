using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static async Task ZeroDatePolicy(CaseContext c, CancellationToken token)
    {
        await using var connection = await c.OpenAsync(token);
        var mode = await c.ScalarAsync(connection, "SELECT @@SESSION.sql_mode", token);
        c.Check(mode is string, "The zero-date profile requires a readable session SQL_MODE.");
        var modes = ((string)mode!).Split(',').Select(value => value.Trim()).ToArray();
        c.Check(!modes.Contains("NO_ZERO_DATE", StringComparer.OrdinalIgnoreCase) &&
            !modes.Contains("NO_ZERO_IN_DATE", StringComparer.OrdinalIgnoreCase),
            "The zero-date profile requires NO_ZERO_DATE and NO_ZERO_IN_DATE to be absent; the runner does not change SQL_MODE.");

        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY, d DATE, dt DATETIME(6)", token);
        c.Check(await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES " +
            "(1,'2024-02-29','2024-02-29 01:02:03.123456')," +
            "(2,'2024-00-12','2024-02-00 01:02:03.123456')," +
            "(3,'0000-00-00','0000-00-00 00:00:00.000000'),(4,NULL,NULL)", token) == 4,
            "The zero-date fixture must insert exactly four rows.");

        var dates = new[] { new OceanBaseDate(2024, 2, 29), new OceanBaseDate(2024, 0, 12), default };
        var times = new[] { new OceanBaseDateTime(dates[0], 1, 2, 3, 123456),
            new OceanBaseDateTime(new OceanBaseDate(2024, 2, 0), 1, 2, 3, 123456), new OceanBaseDateTime(default(OceanBaseDate)) };
        var values = new object[4, 2];
        await Verify(copy: false);
        for (var row = 0; row < 4; row++)
        {
            await using var insert = c.Command(connection, "INSERT INTO " + table + " VALUES (" + (row + 5) + ", ?, ?)");
            CaseContext.Parameter(insert, "d", values[row, 0]).OceanBaseDbType = OceanBaseDbType.Date;
            CaseContext.Parameter(insert, "dt", values[row, 1]).OceanBaseDbType = OceanBaseDbType.DateTime;
            c.Check(await insert.ExecuteNonQueryAsync(token) == 1, "Provider-specific date write-back must insert one row.");
        }
        await Verify(copy: true);

        async Task Verify(bool copy)
        {
            foreach (var prepared in new[] { false, true })
            {
                await using var select = c.Command(connection, "SELECT d, dt FROM " + table +
                    (copy ? " WHERE id>4" : " WHERE id<=4") + " ORDER BY id");
                if (prepared) await select.PrepareAsync(token);
                await using var reader = await select.ExecuteReaderAsync(token);
                c.Check(reader.FieldCount == 2 && reader.GetFieldType(0) == typeof(DateTime) && reader.GetFieldType(1) == typeof(DateTime) &&
                    reader.GetProviderSpecificFieldType(0) == typeof(OceanBaseDate) && reader.GetProviderSpecificFieldType(1) == typeof(OceanBaseDateTime),
                    "DATE/DATETIME metadata must retain the standard and provider-specific types.");
                for (var row = 0; row < 4; row++)
                {
                    c.Check(await reader.ReadAsync(token), "The zero-date query must return all four rows.");
                    for (var column = 0; column < 2; column++)
                    {
                        var actual = reader.GetProviderSpecificValue(column);
                        if (row == 3)
                            c.Check(reader.IsDBNull(column) && actual is DBNull && reader.GetValue(column) is DBNull, "NULL dates must remain NULL.");
                        else
                        {
                            c.Check(!reader.IsDBNull(column), "Zero date components must not become NULL.");
                            if (column == 0) c.Check(actual is OceanBaseDate date && date.Equals(dates[row]), "DATE fields must round-trip without normalization.");
                            else c.Check(actual is OceanBaseDateTime time && time.Equals(times[row]), "DATETIME fields and microseconds must round-trip without normalization.");
                            if (row == 0)
                            {
                                var expected = column == 0 ? dates[row].ToDateTime() : times[row].ToDateTime();
                                c.Check(reader.GetValue(column) is DateTime value && value == expected && reader.GetDateTime(column) == expected,
                                    "Valid dates must retain their standard DateTime conversion.");
                            }
                            else
                            {
                                MustReject(() => reader.GetValue(column));
                                MustReject(() => reader.GetDateTime(column));
                            }
                        }
                        if (!copy && !prepared) values[row, column] = actual;
                    }
                }
                c.Check(!await reader.ReadAsync(token) && !await reader.NextResultAsync(token), "The zero-date query must consume exactly four rows and one result.");
            }
        }

        static void MustReject(Func<object> read)
        {
            try { read(); }
            catch (InvalidCastException) { return; }
            throw new AssertionFailure("Zero date components must reject standard DateTime conversion.");
        }
    }
}
