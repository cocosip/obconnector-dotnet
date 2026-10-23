using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Streams;

public sealed class StreamTests
{
    private const int LargeValueBytes = 65536;

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task BinaryStreamRoundTripSync(string profileName) => BinaryStream(profileName, asynchronous: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public Task BinaryStreamRoundTripAsync(string profileName) => BinaryStream(profileName, asynchronous: true);

    private static async Task BinaryStream(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER PRIMARY KEY, value " + (db.Oracle ? "BLOB" : "LONGBLOB"), CancellationToken.None);
        var bytes = Enumerable.Range(0, LargeValueBytes).Select(i => (byte)(i * 31)).ToArray();
        using var input = new MemoryStream(bytes, false);
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1, " + db.ParameterSql(":value") + ")"))
        {
            DatabaseContext.Parameter(insert, "value", input, DbType.Binary);
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        }
        Assert.True(input.CanRead && input.Position == input.Length,
            "Driver must consume but retain caller stream ownership.");
        byte[]? actual = null;
        OceanBaseLob? lob = null;
        await using (var select = db.Command(connection, "SELECT value FROM " + table + " WHERE id=1"))
        await using (var reader = asynchronous
                ? await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess)
                : select.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            if (db.Oracle && reader.GetFieldType(0) == typeof(OceanBaseLob)) lob = (OceanBaseLob)reader.GetValue(0);
            else
            {
                using var source = reader.GetStream(0);
                using var target = new MemoryStream();
                if (asynchronous) await source.CopyToAsync(target); else source.CopyTo(target);
                actual = target.ToArray();
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        if (lob is not null)
        {
            using (lob)
            using (var source = lob.OpenRead())
            using (var target = new MemoryStream())
            {
                if (asynchronous) await source.CopyToAsync(target); else source.CopyTo(target);
                actual = target.ToArray();
            }
        }
        Assert.NotNull(actual);
        Assert.True(actual!.AsSpan().SequenceEqual(bytes), "Uploaded binary stream must match the stored bytes.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithPrepareExecute), MemberType = typeof(ProfileTheoryData))]
    public async Task OraclePieceStreamUploadPreservesBytes(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Oracle);
        await using var connection = await db.OpenAsync();
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        using var stream = new MemoryStream(bytes, false);
        await using var command = db.Command(connection, "BEGIN :result := UTL_RAW.LENGTH(:value); END;");
        DatabaseContext.Parameter(command, "value", stream, DbType.Binary);
        var output = DatabaseContext.Parameter(command, "result", DBNull.Value, DbType.Int32);
        output.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync();
        DatabaseContext.EqualNumber(output.Value, bytes.Length, "PIECE_DATA upload must preserve the byte count.");
    }
}
