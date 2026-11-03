using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class NamedLobLocatorWireTests
{
    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            if (IntegrationEnvironment.GetProfile(profile).CreateBuilder().UseLobLocators)
                foreach (var asynchronous in new[] { false, true }) data.Add(profile, asynchronous);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task NamedBlobMembersTreatLocatorBytesAsContentWhileRootConstructorsPreserveLocators(string profileName, bool asynchronous)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        OceanBaseConnection? connection = null; OceanBaseTransaction? transaction = null; OceanBaseLob? lob = null;
        var failures = new List<Exception>();
        try
        {
            connection = await db.OpenAsync();
            var name = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT (a BLOB)", CancellationToken.None);
            var procedure = db.NewObject("PROCEDURE");
            await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN " + name + ", n OUT NUMBER, v OUT RAW) IS BEGIN " +
                "n := DBMS_LOB.GETLENGTH(p.a); v := DBMS_LOB.SUBSTR(p.a,32,1); END;", CancellationToken.None);
            var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
            transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
            var type = await connection.GetComplexTypeAsync(owner, name, transaction);
            lob = asynchronous ? await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob, transaction)
                : connection.CreateTemporaryLob(OceanBaseDbType.Blob, transaction);
            var marker = new byte[] { 0, 128, 255 };
            if (asynchronous) await lob.WriteBytesAsync(0, marker, 0, marker.Length); else lob.WriteBytes(0, marker, 0, marker.Length);
            var locator = lob.BindingBytes(); Assert.True(locator.Length > 32);
            Assert.Throws<NotSupportedException>(() => type.CreateValue(lob));
            await using (var root = db.Command(connection, "SELECT DBMS_LOB.GETLENGTH(:p) FROM DUAL", transaction))
            {
                root.Parameters.Add(new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.Blob, Value = lob });
                var length = asynchronous ? await root.ExecuteScalarAsync() : root.ExecuteScalar();
                Assert.Equal(marker.Length, DatabaseContext.IntegerResult(length));
            }
            foreach (var input in new[] { marker, locator })
            {
                var result = await Call(procedure, type.CreateValue(input), constructor: false);
                Assert.Equal(input.Length, result.Length);
                Assert.Equal(input.Take(32).ToArray(), result.Prefix);
                if (ReferenceEquals(input, locator)) Assert.False(marker.SequenceEqual(result.Prefix));
            }
            var constructed = await Call("BEGIN " + procedure + "(" + name + "(:p), :n, :v); END;", lob, constructor: true);
            Assert.Equal(marker.Length, constructed.Length); Assert.Equal(marker, constructed.Prefix);

            async Task<(long Length, byte[] Prefix)> Call(string sql, object input, bool constructor)
            {
                await using var command = db.Command(connection!, sql, transaction);
                command.CommandType = constructor ? CommandType.Text : CommandType.StoredProcedure;
                var parameter = DatabaseContext.Parameter(command, "p", input);
                if (constructor) parameter.OceanBaseDbType = OceanBaseDbType.Blob;
                var length = new OceanBaseParameter { ParameterName = "n", Direction = ParameterDirection.Output, DbType = DbType.Decimal };
                var value = new OceanBaseParameter { ParameterName = "v", Direction = ParameterDirection.Output, DbType = DbType.Binary };
                command.Parameters.Add(length); command.Parameters.Add(value);
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                return (DatabaseContext.IntegerResult(length.Value), Assert.IsType<byte[]>(value.Value));
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (lob != null)
            {
                await Cleanup(async () => { if (asynchronous) await lob.FreeTemporaryAsync(); else lob.FreeTemporary(); });
                await Cleanup(async () => await lob.DisposeAsync());
            }
            if (transaction != null)
            {
                await Cleanup(() => transaction.RollbackAsync());
                await Cleanup(async () => await transaction.DisposeAsync());
            }
            if (connection != null) await Cleanup(async () => await connection.DisposeAsync());
            await Cleanup(async () => await db.DisposeAsync());
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);

        async Task Cleanup(Func<Task> action)
        {
            try { await action(); }
            catch (Exception cleanup) { failures.Add(cleanup); }
        }
    }
}
