using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class PackageLocalRecordTests
{
    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in ProfileTheoryData.OracleWithCreateObjects())
            foreach (var asynchronous in new[] { false, true })
                data.Add(profile, asynchronous);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task PackageLocalRecordWithCursorMemberIsRejectedByServer(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var package = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        db.RegisterDropCleanup("PACKAGE", package);
        var sql = "CREATE PACKAGE " + package + " AS " +
            "TYPE R IS RECORD (C SYS_REFCURSOR, N NUMBER); " +
            "PROCEDURE P(V OUT R); END;";
        using (var create = db.Command(connection, sql))
        {
            if (asynchronous) await create.ExecuteNonQueryAsync();
            else create.ExecuteNonQuery();
        }

        async Task<string?> Scalar(string query)
        {
            using var command = db.Command(connection, query);
            object? value;
            if (asynchronous) value = await command.ExecuteScalarAsync();
            else value = command.ExecuteScalar();
            return Convert.ToString(value);
        }

        var status = await Scalar("SELECT STATUS FROM ALL_OBJECTS WHERE OBJECT_NAME='" + package + "' AND OBJECT_TYPE='PACKAGE'");
        var errorText = await Scalar("SELECT TEXT FROM ALL_ERRORS WHERE NAME='" + package + "' AND TYPE='PACKAGE'");
        Assert.Equal("INVALID", status);
        Assert.Contains("-4007", errorText, StringComparison.Ordinal);
        Assert.Contains("record is cursor type not supported", errorText, StringComparison.OrdinalIgnoreCase);
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task PackageLocalRecordMetadataRemainsVisibleButBindingIsRejected(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var package = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        db.RegisterDropCleanup("PACKAGE", package);
        await db.ExecuteAsync(connection,
            "CREATE PACKAGE " + package + " AS " +
            "TYPE R IS RECORD (N NUMBER, S VARCHAR2(40)); " +
            "PROCEDURE P(V IN R); END;", CancellationToken.None);
        await db.ExecuteAsync(connection,
            "CREATE PACKAGE BODY " + package + " AS " +
            "PROCEDURE P(V IN R) AS BEGIN NULL; END; END;", CancellationToken.None);

        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None))!;
        using (var parameters = await connection.GetSchemaAsync("ProcedureParameters",
                   new string?[] { null, owner, "P", null, package }, CancellationToken.None))
        {
            var record = Assert.Single(parameters.Rows.Cast<DataRow>());
            Assert.Equal("PL/SQL RECORD", record["DATA_TYPE"]);
            Assert.Equal("R", record["TYPE_NAME"]);
            Assert.Equal(package, record["TYPE_SUBNAME"]);
        }

        await using var command = db.Command(connection, package + ".P");
        command.CommandType = CommandType.StoredProcedure;
        if (asynchronous)
            await Assert.ThrowsAsync<NotSupportedException>(() => command.DeriveParametersAsync());
        else
            Assert.Throws<NotSupportedException>(() => command.DeriveParameters());
        Assert.Empty(command.Parameters);
    }
}
