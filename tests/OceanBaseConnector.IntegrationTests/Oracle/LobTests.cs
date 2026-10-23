using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class LobTests
{
    private static bool LobCapable(IntegrationProfile profile)
    {
        var builder = new OceanBaseConnectionStringBuilder(profile.ConnectionString);
        return profile.Oracle && builder.UseOraclePrepareExecute && builder.UseLobLocators;
    }

    public static TheoryData<string, bool> CharacterRows()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(LobCapable))
        {
            data.Add(profile.Name, false);
            data.Add(profile.Name, true);
        }
        return data;
    }

    public static TheoryData<string> ObjectRows()
    {
        var data = new TheoryData<string>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => LobCapable(p) && p.CreateObjects))
            data.Add(profile.Name);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(CharacterRows))]
    public async Task TemporaryLobWriteTruncateRead(string profileName, bool character)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Oracle);
        var connection = await db.OpenAsync();
        db.RegisterCleanup("SESSION", "temporary-lob", () => connection.DisposeAsync().AsTask());
        var lob = await connection.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob);
        db.RegisterCleanup("TEMPORARY_LOB", character ? "CLOB" : "BLOB", async () =>
        {
            try { await lob.FreeTemporaryAsync(CancellationToken.None); }
            finally { await lob.DisposeAsync(); }
        });
        if (character)
        {
            await lob.WriteTextAsync(0, "中文🙂XYZ");
            await lob.TruncateAsync(3);
            using var reader = lob.OpenTextReader();
            Assert.Equal("中文🙂", await reader.ReadToEndAsync());
        }
        else
        {
            var bytes = new byte[] { 0, 1, 128, 255 };
            await lob.WriteBytesAsync(0, bytes, 0, bytes.Length);
            await lob.TruncateAsync(3);
            using var source = lob.OpenRead();
            using var destination = new MemoryStream();
            await source.CopyToAsync(destination);
            Assert.True(destination.ToArray().AsSpan().SequenceEqual(bytes.AsSpan(0, 3)), "BLOB write/truncate/read mismatch.");
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithLobLocators), MemberType = typeof(ProfileTheoryData))]
    public async Task LobHandleEnforcesSingleStreamAndKind(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var connection = await db.OpenAsync();
        db.RegisterCleanup("SESSION", "temporary-lob", () => connection.DisposeAsync().AsTask());
        await using var lob = await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob);
        db.RegisterCleanup("TEMPORARY_LOB", "BLOB", () => lob.DisposeAsync().AsTask());
        Assert.False(lob.IsCharacter);
        Assert.Throws<InvalidOperationException>(() => lob.OpenTextReader());
        using (lob.OpenRead())
        {
            Assert.Throws<InvalidOperationException>(() => lob.OpenRead());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => lob.WriteTextAsync(0, "text"));
        var clob = await connection.CreateTemporaryLobAsync(OceanBaseDbType.Clob);
        db.RegisterCleanup("TEMPORARY_LOB", "CLOB", () => clob.DisposeAsync().AsTask());
        Assert.True(clob.IsCharacter);
        await clob.WriteTextAsync(0, "text");
        await Assert.ThrowsAsync<InvalidOperationException>(() => clob.WriteBytesAsync(0, new byte[] { 1 }, 0, 1));
    }

    [IntegrationTheory]
    [MemberData(nameof(ObjectRows))]
    public async Task LobLocatorParameterRoundTrips(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value BLOB", CancellationToken.None);
        var bytes = Enumerable.Range(0, 512).Select(i => (byte)(i * 7)).ToArray();
        await using var lob = await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob);
        await lob.WriteBytesAsync(0, bytes, 0, bytes.Length);
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1, :value)"))
        {
            DatabaseContext.Parameter(insert, "value", lob);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
        await using var select = db.Command(connection, "SELECT value FROM " + table + " WHERE id=1");
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.IsType<OceanBaseLob>(reader.GetValue(0));
        using var stored = (OceanBaseLob)reader.GetValue(0);
        Assert.Equal((long)bytes.Length, stored.ByteLength);
        using var source = stored.OpenRead();
        using var destination = new MemoryStream();
        await source.CopyToAsync(destination);
        Assert.True(destination.ToArray().AsSpan().SequenceEqual(bytes), "LOB locator parameter must round-trip the bytes.");
        Assert.False(await reader.ReadAsync());
    }
}
