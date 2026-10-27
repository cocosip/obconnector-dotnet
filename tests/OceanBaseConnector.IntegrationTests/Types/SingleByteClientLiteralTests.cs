using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class SingleByteClientLiteralTests
{
    public static TheoryData<string, string, string, string, bool> Cases()
    {
        var cases = new TheoryData<string, string, string, string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(profile => !profile.Oracle))
            foreach (var (name, text, hex) in Samples)
                foreach (var asynchronous in new[] { false, true }) cases.Add(profile.Name, name, text, hex, asynchronous);
        return cases;
    }

    internal static readonly (string Name, string Text, string Hex)[] Samples =
    {
        ("ascii", "ABC", "414243"), ("latin1", "é€", "E980"),
        ("dec8", "¤ŒŸ", "A8D7DD"), ("tis620", "กฺ฿", "A1DADF"),
    };

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public Task GeneratedSingleByteLiteralPreservesValueCollationAndCoercibility(string profile, string name, string text, string hex, bool asynchronous)
        => Verify(IntegrationEnvironment.GetProfile(profile), name, text, hex, asynchronous);

    // The private live runner can invoke the same check with an in-memory profile.
    internal static async Task Verify(IntegrationProfile profile, string name, string text, string hex, bool asynchronous)
    {
        var builder = profile.CreateBuilder(); builder.CharacterSet = name;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        using var state = connection.CreateCommand();
        state.CommandText = "SELECT @@character_set_client, @@character_set_connection, @@collation_connection, @@sql_mode";
        var values = new string[4];
        using (var reader = asynchronous ? await state.ExecuteReaderAsync() : state.ExecuteReader())
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            for (var i = 0; i < values.Length; i++) values[i] = reader.GetString(i);
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        var encoding = ClientCharacterSet.Parse(name);
        var context = MySqlLiteralContext.Parse(values[0], values[1], values[2], values[3], encoding);
        var suffix = "'\\\b\t\n\r\u001a  x'); SELECT 999;--";
        if (!values[3].Split(',').Contains("NO_BACKSLASH_ESCAPES", StringComparer.OrdinalIgnoreCase)) suffix += "\0";
        else Assert.Throws<NotSupportedException>(() => context.Literal("\0"));
        var value = text + suffix;
        using var command = connection.CreateCommand();
        var parameters = Enumerable.Range(0, 4).Select(_ => new OceanBaseParameter { Value = value }).ToArray();
        command.CommandText = ClientPreparedSql.Build(new ParameterBindingPlan("SELECT ? AS v, COLLATION(?) AS c, COERCIBILITY(?) AS k, HEX(?) AS h", parameters),
            OceanBaseCompatibilityMode.MySql, context, encoding);
        using var result = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.True(asynchronous ? await result.ReadAsync() : result.Read());
        Assert.Equal(value, result.GetString(0)); Assert.Equal(values[2], result.GetString(1));
        Assert.Equal(4, Convert.ToInt32(result.GetValue(2), CultureInfo.InvariantCulture));
        Assert.Equal(hex + Convert.ToHexString(Encoding.ASCII.GetBytes(suffix)), result.GetString(3));
        Assert.False(asynchronous ? await result.ReadAsync() : result.Read());
        Assert.False(asynchronous ? await result.NextResultAsync() : result.NextResult());
    }
}
