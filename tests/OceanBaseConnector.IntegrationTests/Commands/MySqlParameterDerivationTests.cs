using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Commands;

public sealed class MySqlParameterDerivationTests
{
    public static TheoryData<string, bool> Profiles()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            if (!profile.Oracle && profile.CreateObjects)
            {
                rows.Add(profile.Name, true);
                rows.Add(profile.Name, false);
            }
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task MySqlProcedureDerivesInputOutputAndInputOutputParameters(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var name = db.NewObject("PROCEDURE", registerCleanup: false);
        await db.ExecuteAsync(connection,
            "CREATE PROCEDURE " + name + " (IN p_input INT, OUT p_output INT, INOUT p_state VARCHAR(40)) " +
            "BEGIN SET p_output = p_input + 1; SET p_state = CONCAT(p_state, '-done'); END",
            CancellationToken.None);
        db.RegisterDropCleanup("PROCEDURE", name);

        await using var command = db.Command(connection, name);
        command.CommandType = CommandType.StoredProcedure;
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();

        var parameters = command.Parameters.Cast<OceanBaseParameter>().ToArray();
        Assert.Equal(3, parameters.Length);
        var input = Assert.Single(parameters, p => string.Equals(p.ParameterName, "p_input", StringComparison.OrdinalIgnoreCase));
        var output = Assert.Single(parameters, p => string.Equals(p.ParameterName, "p_output", StringComparison.OrdinalIgnoreCase));
        var inout = Assert.Single(parameters, p => string.Equals(p.ParameterName, "p_state", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ParameterDirection.Input, input.Direction);
        Assert.Equal(ParameterDirection.Output, output.Direction);
        Assert.Equal(ParameterDirection.InputOutput, inout.Direction);
        Assert.Equal(OceanBaseDbType.Int32, input.OceanBaseDbType);
        Assert.Equal(OceanBaseDbType.Int32, output.OceanBaseDbType);
        Assert.Equal(OceanBaseDbType.String, inout.OceanBaseDbType);
        Assert.Equal(40, inout.Size);
        Assert.All(parameters, parameter => Assert.Equal(DBNull.Value, parameter.Value));
    }
}
