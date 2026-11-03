using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class UnicodeRoutineTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.CreateBuilder().UseOraclePrepareExecute)
        from asynchronous in new[] { false, true }
        from packaged in new[] { false, true }
        select new object[] { profile.Name, asynchronous, packaged };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task UnquotedUnicodeRoutineDerivesAndExecutes(string profileName, bool asynchronous, bool packaged)
        => await DeriveAndExecute(profileName, asynchronous, packaged, unicodeParameters: false);

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task UnicodeParametersDeriveAndExecuteWithoutRenaming(string profileName, bool asynchronous, bool packaged)
        => await DeriveAndExecute(profileName, asynchronous, packaged, unicodeParameters: true);

    private static async Task DeriveAndExecute(string profileName, bool asynchronous, bool packaged, bool unicodeParameters)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = "诊断" + db.NewObject("PROCEDURE", registerCleanup: false);
        var signature = unicodeParameters ? "(输入 IN NUMBER, 输出 OUT NUMBER, 累计 IN OUT NUMBER)" : "(P_INPUT IN NUMBER, P_OUTPUT OUT NUMBER)";
        var body = unicodeParameters ? " AS BEGIN 输出 := 输入 + 1; 累计 := 累计 + 输入; END;" : " AS BEGIN P_OUTPUT := P_INPUT + 1; END;";
        if (packaged)
        {
            await db.ExecuteAsync(connection, "CREATE PACKAGE " + name + " AS PROCEDURE 查询" + signature + "; END;", CancellationToken.None);
            db.RegisterCleanup("PACKAGE", name, async () =>
            {
                await using var cleanup = await db.OpenAsync();
                await db.ExecuteAsync(cleanup, "DROP PACKAGE " + name, CancellationToken.None);
            });
            await db.ExecuteAsync(connection, "CREATE PACKAGE BODY " + name + " AS PROCEDURE 查询" + signature + body + " END;", CancellationToken.None);
        }
        else
        {
            await db.ExecuteAsync(connection, "CREATE PROCEDURE " + name + signature + body, CancellationToken.None);
            db.RegisterDropCleanup("PROCEDURE", name);
        }
        using var directory = await connection.GetSchemaAsync("Procedures", new string?[] { null, null, packaged ? "查询" : name, null, packaged ? name : null });
        Assert.Single(directory.Rows.Cast<DataRow>());
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        await using var command = db.Command(connection, "\"" + owner.Replace("\"", "\"\"") + "\"." + name + (packaged ? ".查询" : ""));
        command.CommandType = CommandType.StoredProcedure;
        var old = new OceanBaseParameter { ParameterName = "old", Value = 9 }; command.Parameters.Add(old);
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        Assert.Null(old.Owner); Assert.Equal(unicodeParameters ? 3 : 2, command.Parameters.Count);
        Assert.Equal(unicodeParameters ? new[] { "输入", "输出", "累计" } : new[] { "P_INPUT", "P_OUTPUT" }, command.Parameters.Cast<OceanBaseParameter>().Select(p => p.ParameterName));
        Assert.Equal(ParameterDirection.Input, command.Parameters[0].Direction); Assert.Equal(ParameterDirection.Output, command.Parameters[1].Direction);
        if (unicodeParameters) Assert.Equal(ParameterDirection.InputOutput, command.Parameters[2].Direction);
        for (var i = 0; i < 2; i++)
        {
            if (i == 1) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
            command.Parameters[0].Value = 41m + i; command.Parameters[1].Value = DBNull.Value;
            if (unicodeParameters) command.Parameters[2].Value = 10m + i;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            DatabaseContext.EqualNumber(command.Parameters[1].Value, 42 + i, "Unquoted Unicode procedure output mismatch.");
            if (unicodeParameters) DatabaseContext.EqualNumber(command.Parameters[2].Value, 51 + i * 2, "Unicode INOUT mismatch.");
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Connection must remain reusable.");
    }

    public static IEnumerable<object[]> BindingRows() => Rows().Where(row => !(bool)row[2]).Select(row => new[] { row[0], row[1] });

    [IntegrationTheory]
    [MemberData(nameof(BindingRows))]
    public async Task UnicodeLabelsStoreValuesInPreparedDmlAndScripts(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var observer = await db.OpenAsync();
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY,v VARCHAR2(100)", CancellationToken.None);
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = true;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        const string value = "中文'); DELETE -- :内容 ?";
        using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:编号,:内容)"))
        {
            DatabaseContext.Parameter(insert, ":内容", value); DatabaseContext.Parameter(insert, "编号", 1);
            if (asynchronous) await insert.PrepareAsync(); else insert.Prepare();
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        }
        using (var script = db.Command(connection, "INSERT INTO " + table + " VALUES (:编号,:内容); INSERT INTO " + table + " VALUES (:编号+1,:内容)"))
        {
            DatabaseContext.Parameter(script, "内容", value); DatabaseContext.Parameter(script, ":编号", 2);
            Assert.Equal(2, asynchronous ? await script.ExecuteNonQueryAsync() : script.ExecuteNonQuery());
        }
        using var query = db.Command(observer, "SELECT id,v FROM " + table + " ORDER BY id");
        using var rows = await query.ExecuteReaderAsync();
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(await rows.ReadAsync()); DatabaseContext.EqualNumber(rows.GetValue(0), i, "Unicode DML id mismatch.");
            Assert.Equal(value, rows.GetString(1));
        }
        Assert.False(await rows.ReadAsync());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Connection must remain reusable.");
    }
}
