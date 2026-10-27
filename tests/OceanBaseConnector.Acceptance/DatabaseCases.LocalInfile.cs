using System;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static async Task LocalInfile(CaseContext c, bool async, CancellationToken token)
    {
        await using var connection = c.CreateConnection();
        const string filename = "oba-local-infile.csv";
        var payload = Encoding.UTF8.GetBytes("1,alpha\n2,中文\n");
        var requestedFilename = string.Empty;
        connection.LocalInfileStreamFactory = name =>
        {
            requestedFilename = name;
            return new MemoryStream(payload, writable: false);
        };
        if (async) await connection.OpenAsync(token); else connection.Open();
        var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY, value VARCHAR(100)", token);

        await using (var command = c.Command(connection,
            "LOAD DATA LOCAL INFILE '" + filename + "' INTO TABLE " + table +
            " FIELDS TERMINATED BY ',' LINES TERMINATED BY '\\n'"))
        {
            var affected = async ? await command.ExecuteNonQueryAsync(token) : command.ExecuteNonQuery();
            c.EqualNumber(affected, 2, "LOCAL INFILE must report two affected rows.");
        }
        c.Check(requestedFilename == filename, "LOCAL INFILE must pass the server filename to the factory.");

        await using (var command = c.Command(connection, "SELECT id, value FROM " + table + " ORDER BY id"))
        await using (var reader = async ? await command.ExecuteReaderAsync(token) : command.ExecuteReader())
        {
            c.Check(async ? await reader.ReadAsync(token) : reader.Read(), "The first uploaded row must exist.");
            c.EqualNumber(reader.GetValue(0), 1, "The first uploaded id must be one.");
            c.Check(reader.GetString(1) == "alpha", "The first uploaded value must be alpha.");
            c.Check(async ? await reader.ReadAsync(token) : reader.Read(), "The second uploaded row must exist.");
            c.EqualNumber(reader.GetValue(0), 2, "The second uploaded id must be two.");
            c.Check(reader.GetString(1) == "中文", "The second uploaded value must preserve UTF-8.");
            c.Check(!(async ? await reader.ReadAsync(token) : reader.Read()), "The upload query must contain exactly two rows.");
        }

        await using var followUp = c.Command(connection, c.SelectOne);
        var result = async ? await followUp.ExecuteScalarAsync(token) : followUp.ExecuteScalar();
        c.EqualNumber(result, 1, "The connection must remain reusable after LOCAL INFILE.");
    }
}
