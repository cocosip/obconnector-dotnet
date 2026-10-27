using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.IntegrationTests.Types;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexNationalClientEncodingTests
{
    internal static readonly (string Name, string Text, string Hex)[] Samples =
        new[] { ("utf8mb3", "中é", "E4B8ADC3A9") }.Concat(SingleByteClientLiteralTests.Samples).Concat(DbcsClientLiteralTests.Samples).ToArray();

    public static TheoryData<string, string, string, string, bool> Cases()
    {
        var cases = new TheoryData<string, string, string, string, bool>();
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var (name, text, _) in Samples)
                foreach (var nationalType in new[] { "NCHAR", "NVARCHAR2" })
                    foreach (var asynchronous in new[] { false, true }) cases.Add(profile, name, text, nationalType, asynchronous);
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public Task AnonymousNationalArrayInputsPreserveClientEncodedText(string profile, string charset, string text, string nationalType, bool asynchronous)
        => Verify(IntegrationEnvironment.GetProfile(profile), charset, text, nationalType, asynchronous);

    internal static async Task Verify(IntegrationProfile profile, string charset, string text, string nationalType, bool asynchronous)
    {
        if (!profile.Oracle || !profile.CreateObjects) throw new InvalidOperationException("An Oracle profile with explicit test-object permission is required.");
        if (nationalType != "NCHAR" && nationalType != "NVARCHAR2") throw new ArgumentException("A supported national type is required.", nameof(nationalType));
        var builder = profile.CreateBuilder(); builder.CharacterSet = charset; builder.UseOraclePrepareExecute = true;
        var selected = new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, CreateObjects = profile.CreateObjects, ConnectionString = builder.ConnectionString };
        await using var db = new DatabaseContext(selected);
        await using var connection = await db.OpenAsync();
        text += "\\'  ";
        var width = text.EnumerateRunes().Count();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + nationalType + "(" + width + ")", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        using (var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:id, :v)", transaction))
        {
            DatabaseContext.Parameter(command, "id", connection.CreateArrayType("NUMBER", transaction).CreateValue(1, 2));
            DatabaseContext.Parameter(command, "v", connection.CreateArrayType(nationalType, transaction).CreateValue(text, null));
            var result = asynchronous ? await command.ExecuteArrayAsync(2) : command.ExecuteArray(2);
            Assert.True(result.IsComplete); Assert.Equal(2, result.RecordsAffected);
        }
        using (var command = db.Command(connection, "SELECT v AS value FROM " + table + " ORDER BY id", transaction))
        using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(text, reader.GetString(0));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        await transaction.RollbackAsync();
    }
}
