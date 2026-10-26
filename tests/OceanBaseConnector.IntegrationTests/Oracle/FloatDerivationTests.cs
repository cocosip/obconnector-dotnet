using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class FloatDerivationTests
{
    public static TheoryData<string, bool, bool> Profiles()
    {
        var rows = new TheoryData<string, bool, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            if (profile.Oracle && profile.CreateObjects)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var function in new[] { false, true })
                        rows.Add(profile.Name, asynchronous, function);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task FloatProcedureAndFunctionDerivationPreserveInputOutputAndExactBindings(string profileName, bool asynchronous, bool function)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.UseOraclePrepareExecute = true;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var kind = function ? "FUNCTION" : "PROCEDURE";
        var name = function ? "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant() : db.NewObject(kind);
        if (function)
            db.RegisterCleanup(kind, name, async () =>
            {
                await using var cleanup = await db.OpenAsync();
                await db.ExecuteAsync(cleanup, "DROP FUNCTION " + name, CancellationToken.None);
            });
        await db.ExecuteAsync(connection, "CREATE " + kind + " " + name +
            " (p_input IN FLOAT, p_output OUT FLOAT, p_inout IN OUT FLOAT) " + (function ? "RETURN FLOAT " : "") +
            "AS BEGIN p_output := p_input; p_inout := p_input + 0.5; " + (function ? "RETURN p_input; " : "") + "END;", CancellationToken.None);
        await using var command = db.Command(connection, name); command.CommandType = CommandType.StoredProcedure;
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        var parameters = command.Parameters.Cast<OceanBaseParameter>().ToArray();
        Assert.Equal(function ? 4 : 3, parameters.Length);
        Assert.All(parameters, p => { Assert.Equal(OceanBaseDbType.Decimal, p.OceanBaseDbType); Assert.Equal(0, p.Precision); Assert.Null(p.DecimalScale); Assert.Equal(DBNull.Value, p.Value); });
        OceanBaseParameter Parameter(string parameterName) => Assert.Single(parameters, p => string.Equals(parameterName, p.ParameterName, StringComparison.OrdinalIgnoreCase));
        var input = Parameter("P_INPUT"); var output = Parameter("P_OUTPUT"); var inout = Parameter("P_INOUT");
        Assert.Equal(ParameterDirection.Input, input.Direction); Assert.Equal(ParameterDirection.Output, output.Direction); Assert.Equal(ParameterDirection.InputOutput, inout.Direction);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture);
        using var directory = await connection.GetSchemaAsync("ProcedureParameters", new string?[] { null, owner, name });
        Assert.Equal(parameters.Length, directory.Rows.Count);
        Assert.All(directory.Rows.Cast<DataRow>(), row => Assert.Contains((string)row["DATA_TYPE"], new[] { "FLOAT", "NUMBER_FLOAT" }));
        Assert.Equal("INOUT", Assert.Single(directory.Rows.Cast<DataRow>(), row => string.Equals(row["PARAMETER_NAME"] as string, "P_INOUT", StringComparison.OrdinalIgnoreCase))["PARAMETER_MODE"]);
        foreach (var value in new[] { 7.2500m, 700m })
        {
            input.Value = new OceanBaseDecimal(value); inout.Value = new OceanBaseDecimal(1.25m);
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            Assert.Equal(value, Assert.IsType<OceanBaseDecimal>(output.Value).ToDecimal());
            Assert.Equal(value + 0.5m, Assert.IsType<OceanBaseDecimal>(inout.Value).ToDecimal());
            if (function)
            {
                var returned = Parameter("RETURN_VALUE"); Assert.Equal(ParameterDirection.ReturnValue, returned.Direction);
                Assert.Equal(value, Assert.IsType<OceanBaseDecimal>(returned.Value).ToDecimal());
            }
        }
    }
}
