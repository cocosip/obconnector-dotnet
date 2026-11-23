using System;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class CompatibilityModeTests
{
    public static TheoryData<string, string, bool, bool> ExecutionCases()
    {
        var rows = new TheoryData<string, string, bool, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
        foreach (var configuration in new[] { "omitted", "auto", "matching" })
        foreach (var asynchronous in new[] { false, true })
        foreach (var prepared in new[] { false, true })
            rows.Add(profile.Name, configuration, asynchronous, prepared);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(ExecutionCases))]
    public async Task ConfigurationResolvesToActualModeForParametersAndExecution(
        string profileName, string configuration, bool asynchronous, bool prepared)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.Pooling = false;
        if (configuration == "omitted") builder.Remove(OceanBaseConnectionStringBuilder.CompatibilityModeKey);
        else builder.CompatibilityMode = configuration == "auto" ? OceanBaseCompatibilityMode.Auto : profile.Mode;

        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        Assert.Null(connection.CompatibilityMode);
        using (var closedCommand = connection.CreateCommand())
            Assert.Null(((OceanBaseParameter)closedCommand.CreateParameter()).CompatibilityMode);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        Assert.Equal(profile.Mode, connection.CompatibilityMode);
        Assert.NotEqual(OceanBaseCompatibilityMode.Auto, connection.CompatibilityMode);

        await using (var command = connection.CreateCommand())
        {
            var number = (OceanBaseParameter)command.CreateParameter();
            number.ParameterName = "number_value";
            number.DbType = DbType.Decimal;
            number.Precision = 18;
            number.Scale = 4;
            number.Value = 123.4567m;
            var text = (OceanBaseParameter)command.CreateParameter();
            text.ParameterName = "text_value";
            text.DbType = DbType.String;
            text.Value = "中文🙂'\\?";
            var missing = (OceanBaseParameter)command.CreateParameter();
            missing.ParameterName = "null_value";
            missing.DbType = DbType.Int32;
            missing.Value = DBNull.Value;
            foreach (var parameter in new[] { number, text, missing })
            {
                Assert.Equal(connection.CompatibilityMode, parameter.CompatibilityMode);
                command.Parameters.Add(parameter);
            }
            command.CommandText = number.CompatibilityMode == OceanBaseCompatibilityMode.Oracle
                ? "SELECT :number_value, :text_value, :null_value FROM DUAL"
                : "SELECT CAST(? AS DECIMAL(18,4)), ?, ?";
            if (prepared)
            {
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            }
            await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(123.4567m, reader.GetDecimal(0));
            Assert.Equal(text.Value, reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        }
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
        Assert.Null(connection.CompatibilityMode);
    }

    public static TheoryData<string, bool> MismatchCases()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
        foreach (var asynchronous in new[] { false, true })
            rows.Add(profile.Name, asynchronous);
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(MismatchCases))]
    public async Task MismatchedModeFailsBeforePublishingAnActualMode(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder();
        builder.Pooling = false;
        builder.CompatibilityMode = profile.Oracle ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        var error = asynchronous
            ? await Assert.ThrowsAsync<OceanBaseException>(connection.OpenAsync)
            : Assert.Throws<OceanBaseException>(connection.Open);
        Assert.Contains("CompatibilityMode", error.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(connection.CompatibilityMode);
    }
}
