using System;
using System.Data;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class NumberDerivationTests
{
    public static TheoryData<string, bool, bool, bool> Cases()
    {
        var cases = new TheoryData<string, bool, bool, bool>();
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var function in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true }) cases.Add(profile, function, prepared, asynchronous);
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task UnconstrainedNumbersDeriveAndPreserveExactOutputsAndStorage(string profileName, bool function, bool prepared, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.UseOraclePrepareExecute = true; builder.UseLobLocators = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v NUMBER", CancellationToken.None);
        var name = db.NewObject("PROCEDURE", registerCleanup: !function);
        var kind = function ? "FUNCTION" : "PROCEDURE";
        if (function) db.RegisterCleanup(kind, name, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP FUNCTION " + name, CancellationToken.None);
        });
        await db.ExecuteAsync(connection, "CREATE " + kind + " " + name +
            " (p_id IN NUMBER,p_input IN NUMBER,p_output OUT NUMBER,p_inout IN OUT NUMBER) " + (function ? "RETURN NUMBER " : "") +
            "AS BEGIN p_output:=p_input; p_inout:=p_input; INSERT INTO " + table + " VALUES (p_id,p_input); " +
            (function ? "RETURN p_input; " : "") + "END;", CancellationToken.None);
        using var directory = await connection.GetSchemaAsync("ProcedureParameters", new string?[] { null, null, name });
        Assert.Equal(function ? 5 : 4, directory.Rows.Count);
        Assert.All(directory.Rows.Cast<DataRow>(), row =>
        {
            Assert.Equal("NUMBER", row["DATA_TYPE"]); Assert.Equal(DBNull.Value, row["NUMERIC_PRECISION"]);
            Assert.Equal(-85, row["NUMERIC_SCALE"]);
        });
        await using var command = db.Command(connection, name); command.CommandType = CommandType.StoredProcedure;
        var original = DatabaseContext.Parameter(command, "old", 42);
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        Assert.Null(original.Owner);
        var parameters = command.Parameters.Cast<OceanBaseParameter>().ToArray();
        Assert.Equal(function ? 5 : 4, parameters.Length);
        Assert.All(parameters, p => { Assert.Equal(OceanBaseDbType.Decimal, p.OceanBaseDbType); Assert.Equal(0, p.Precision); Assert.Null(p.DecimalScale); Assert.Equal(DBNull.Value, p.Value); });
        OceanBaseParameter Parameter(string key) => Assert.Single(parameters, p => string.Equals(p.ParameterName, key, StringComparison.OrdinalIgnoreCase));
        var id = Parameter("P_ID"); var input = Parameter("P_INPUT"); var output = Parameter("P_OUTPUT"); var inout = Parameter("P_INOUT");
        var returned = function ? Parameter("RETURN_VALUE") : null;
        Assert.Equal(ParameterDirection.Input, id.Direction); Assert.Equal(ParameterDirection.Input, input.Direction);
        Assert.Equal(ParameterDirection.Output, output.Direction); Assert.Equal(ParameterDirection.InputOutput, inout.Direction);
        if (returned != null) Assert.Equal(ParameterDirection.ReturnValue, returned.Direction);
        id.Value = 1m; output.Value = new OceanBaseDecimal(99m); inout.Value = new OceanBaseDecimal(111m);
        if (returned != null) returned.Value = new OceanBaseDecimal(98m);
        if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
        AssertNumber(new OceanBaseDecimal(99m), output.Value); AssertNumber(new OceanBaseDecimal(111m), inout.Value);
        if (returned != null) AssertNumber(new OceanBaseDecimal(98m), returned.Value);
        var session = connection.GetOpenSession();
        OceanBaseDecimal?[] values = { null, new OceanBaseDecimal(7.2500m),
            new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345678"), 0),
            new OceanBaseDecimal(BigInteger.One, 130), null, new OceanBaseDecimal(BigInteger.One, -125) };
        for (var i = 0; i < values.Length; i++)
        {
            id.Value = (decimal)(i + 1); input.Value = values[i].HasValue ? (object)values[i]!.Value : i == 0 ? null : DBNull.Value;
            inout.Value = new OceanBaseDecimal(111m);
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            AssertNumber(values[i], output.Value); AssertNumber(values[i], inout.Value);
            if (returned != null) AssertNumber(values[i], returned.Value);
            Assert.Same(session, connection.GetOpenSession());
        }
        id.Value = 1m; input.Value = new OceanBaseDecimal(777m); inout.Value = new OceanBaseDecimal(111m);
        var oldOutput = output.Value; var oldReturn = returned?.Value;
        var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync())
            : Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
        Assert.Equal(1, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
        Assert.Equal(oldOutput, output.Value); AssertNumber(new OceanBaseDecimal(111m), inout.Value);
        if (returned != null) Assert.Equal(oldReturn, returned.Value);
        Assert.Same(session, connection.GetOpenSession());
        Assert.Equal(6, await db.CountAsync(connection, table, CancellationToken.None));
        id.Value = 7m;
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        AssertNumber(new OceanBaseDecimal(777m), output.Value); AssertNumber(new OceanBaseDecimal(777m), inout.Value);
        if (returned != null) AssertNumber(new OceanBaseDecimal(777m), returned.Value);
        Assert.Same(session, connection.GetOpenSession());
        await using var observer = await db.OpenAsync();
        foreach (var binary in new[] { false, true })
            foreach (var sequential in new[] { false, true })
            {
                await using var query = db.Command(observer, "SELECT id,v FROM " + table + " ORDER BY id");
                if (binary) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
                await using var reader = asynchronous ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                var expected = values.Concat(new OceanBaseDecimal?[] { new OceanBaseDecimal(777m) }).ToArray();
                for (var i = 0; i < expected.Length; i++)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), i + 1, "Derived NUMBER storage order mismatch.");
                    AssertNumber(expected[i], reader.GetProviderSpecificValue(1));
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
    }

    private static void AssertNumber(OceanBaseDecimal? expected, object? value)
    {
        if (!expected.HasValue) { Assert.Equal(DBNull.Value, value); return; }
        var actual = Assert.IsType<OceanBaseDecimal>(value);
        var scale = Math.Max(expected.Value.Scale, actual.Scale);
        Assert.Equal(expected.Value.Coefficient * BigInteger.Pow(10, scale - expected.Value.Scale),
            actual.Coefficient * BigInteger.Pow(10, scale - actual.Scale));
    }
}
