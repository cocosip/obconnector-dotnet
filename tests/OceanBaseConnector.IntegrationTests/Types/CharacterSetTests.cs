using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class CharacterSetTests
{
    public static TheoryData<string, string, string, string> Cases()
    {
        var cases = new TheoryData<string, string, string, string>();
        foreach (var profile in IntegrationEnvironment.Profiles)
        {
            if (profile.Oracle) continue;
            foreach (var (charset, hex, expected) in new[]
            {
                ("ascii", "4142432020", "ABC  "),
                ("latin1", "E980919F2020", "é€‘Ÿ  "),
                ("gbk", "D6D0CEC42020", "中文  "),
                ("gb18030", "A8BC8135F437D6D0CEC49439FC362020", "ḿ\ue7c7中文😀  "),
                ("utf16", "FEFF4E2DD83DDE0000200020", "\ufeff中😀  "),
                ("utf16le", "FFFE2D4E3DD800DE20002000", "\ufeff中😀  "),
            })
                cases.Add(profile.Name, charset, hex, expected);
        }
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task ExplicitResultEncodingsPreserveTextAndPreparedValues(string profileName, string charset, string hex, string expected)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        try
        {
            // UTF-16 cannot be a session result charset; NULL preserves each expression's source encoding.
            var resultCharset = charset is "utf16" or "utf16le" ? "NULL" : charset;
            await db.ExecuteAsync(connection, "SET character_set_results = " + resultCharset, CancellationToken.None);
            foreach (var binary in new[] { false, true })
                foreach (var sequential in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                    {
                        await using var command = db.Command(connection, "SELECT CONVERT(X'" + hex + "' USING " + charset + "), NULL, 42");
                        if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                        await using var reader = asynchronous
                            ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                            : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                        Assert.Equal(typeof(string), reader.GetFieldType(0));
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        using var text = reader.GetTextReader(0);
                        Assert.Equal(expected, asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
                        Assert.True(reader.IsDBNull(1)); Assert.Equal(42L, reader.GetInt64(2));
                        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    }
            await db.ExecuteAsync(connection, "SET character_set_results = utf8mb4", CancellationToken.None);
            Assert.Equal("中文", await db.ScalarAsync(connection, "SELECT '中文'", CancellationToken.None));
        }
        catch (OceanBaseException error)
        {
            throw new InvalidOperationException("Result charset " + charset + " server error " + error.ServerErrorCode + ", SQLSTATE " + error.SqlState, error);
        }
    }
}
