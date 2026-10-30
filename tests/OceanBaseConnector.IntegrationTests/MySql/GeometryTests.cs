using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.MySql;

public sealed class GeometryTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var profile in ProfileTheoryData.MySqlWithCreateObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    yield return new object[] { profile, asynchronous, prepared };
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task GeometryBytesRetainSridAndWkbThroughBinaryParameters(string profileName, bool asynchronous, bool prepared)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var source = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,g GEOMETRY", CancellationToken.None);
        var target = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,g GEOMETRY", CancellationToken.None);
        using (var types = connection.GetSchema("DataTypes"))
        {
            var geometry = Assert.Single(types.Rows.Cast<DataRow>(), r => (string)r["TypeName"] == "GEOMETRY");
            Assert.True((bool)geometry["IsSupportedForParameters"]);
            Assert.Equal((int)OceanBaseDbType.Binary, geometry["ProviderDbType"]);
        }
        var shapes = new[] { "POINT(1 2)", "LINESTRING(0 0,1 1,2 3)", "POLYGON((0 0,0 4,4 4,4 0,0 0))", "MULTIPOINT((1 2),(3 4))",
            "MULTILINESTRING((0 0,1 1),(2 2,3 3))", "MULTIPOLYGON(((0 0,0 4,4 4,4 0,0 0)))",
            "GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))", "GEOMETRYCOLLECTION EMPTY",
            "LINESTRING(" + string.Join(",", Enumerable.Range(0, 300).Select(i => i + " " + i)) + ")" };
        for (var i = 0; i < shapes.Length; i++)
        {
            try { await db.ExecuteAsync(connection, "INSERT INTO " + source + " VALUES (" + (i + 1) + ",ST_GeomFromText('" + shapes[i] + "',0))", CancellationToken.None); }
            catch (OceanBaseException error) { throw new InvalidOperationException($"GEOMETRY seed {i}: code={error.ServerErrorCode}, state={error.SqlState}.", error); }
        }
        var bytes = new List<byte[]>(); var srids = new List<int>(); var wkbs = new List<byte[]>();
        using (var query = db.Command(connection, "SELECT g,ST_SRID(g),ST_AsBinary(g) FROM " + source + " ORDER BY id"))
        using (var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader())
        {
            Assert.Equal("GEOMETRY", reader.GetDataTypeName(0));
            Assert.Equal(typeof(byte[]), reader.GetFieldType(0));
            Assert.Equal((int)OceanBaseDbType.Binary, reader.GetColumnSchema()[0]["ProviderType"]);
            for (var i = 0; i < shapes.Length; i++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                var raw = reader.GetFieldValue<byte[]>(0); var srid = (int)DatabaseContext.IntegerResult(reader.GetValue(1));
                var wkb = reader.GetFieldValue<byte[]>(2);
                Assert.Equal(0, srid);
                Assert.Equal(new[] { (byte)srid, (byte)(srid >> 8), (byte)(srid >> 16), (byte)(srid >> 24) }.Concat(wkb), raw);
                bytes.Add(raw); srids.Add(srid); wkbs.Add(wkb);
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        // This literal independently fixes the SRID=0 POINT(1 2) result representation.
        Assert.Equal(Convert.FromHexString("000000000101000000000000000000F03F0000000000000040"), bytes[0]);
        Assert.True(bytes[bytes.Count - 1].Length > 251);
        var session = connection.GetOpenSession();
        using (var insert = db.Command(connection, "INSERT INTO " + target + " VALUES (?,?)"))
        {
            var id = DatabaseContext.Parameter(insert, "", 0, DbType.Int32);
            var geometry = DatabaseContext.Parameter(insert, "", null, DbType.Binary); geometry.Size = 1;
            if (prepared) { if (asynchronous) await insert.PrepareAsync(); else insert.Prepare(); }
            for (var i = 0; i <= bytes.Count; i++)
            {
                id.Value = i; geometry.Value = i == 0 ? null : bytes[i - 1];
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
                Assert.Same(session, connection.GetOpenSession());
            }
            id.Value = bytes.Count + 1;
            foreach (var invalid in new[] { Array.Empty<byte>(), new byte[4], bytes[0].Take(bytes[0].Length - 1).ToArray(), wkbs[0] })
            {
                geometry.Value = invalid;
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => insert.ExecuteNonQueryAsync()) : Assert.Throws<OceanBaseException>(() => insert.ExecuteNonQuery());
                Assert.NotEqual(0, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Same(session, connection.GetOpenSession());
                Assert.Equal(bytes.Count + 1, await db.CountAsync(connection, target, CancellationToken.None));
            }
            geometry.Value = DBNull.Value;
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            Assert.Same(session, connection.GetOpenSession());
        }
        await using var observer = await db.OpenAsync();
        foreach (var preparedRead in new[] { false, true })
            foreach (var sequential in new[] { false, true })
            {
                using var query = db.Command(observer, "SELECT id,g,ST_SRID(g),ST_AsBinary(g) FROM " + target + " ORDER BY id");
                if (preparedRead) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
                using var reader = asynchronous ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default) : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.Equal("GEOMETRY", reader.GetDataTypeName(1)); Assert.Equal(typeof(byte[]), reader.GetFieldType(1));
                for (var i = 0; i <= bytes.Count + 1; i++)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    DatabaseContext.EqualNumber(reader.GetValue(0), i, "GEOMETRY storage order mismatch.");
                    if (i == 0 || i == bytes.Count + 1) { Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); Assert.True(reader.IsDBNull(3)); }
                    else
                    {
                        using var stream = reader.GetStream(1); using var copy = new System.IO.MemoryStream();
                        if (asynchronous) await stream.CopyToAsync(copy); else stream.CopyTo(copy);
                        Assert.Equal(bytes[i - 1], copy.ToArray());
                        DatabaseContext.EqualNumber(reader.GetValue(2), srids[i - 1], "GEOMETRY SRID mismatch.");
                        Assert.Equal(wkbs[i - 1], reader.GetFieldValue<byte[]>(3));
                    }
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task DerivedGeometryRoutineArgumentsPreserveInputsAndAtomicOutputs(string profileName, bool asynchronous, bool prepared)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var table = await db.TableAsync(connection, "id INT PRIMARY KEY,g GEOMETRY", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(IN n INT,IN p GEOMETRY,INOUT q GEOMETRY,OUT r GEOMETRY) BEGIN " +
            "SET r=q; SET q=p; INSERT INTO " + table + " VALUES(n,p); END", CancellationToken.None);
        using var command = db.Command(connection, procedure); command.CommandType = CommandType.StoredProcedure;
        if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
        OceanBaseParameter Parameter(string name) => Assert.Single(command.Parameters.Cast<OceanBaseParameter>(), p => string.Equals(p.ParameterName, name, StringComparison.OrdinalIgnoreCase));
        var id = Parameter("n"); var input = Parameter("p"); var inout = Parameter("q"); var output = Parameter("r");
        Assert.Equal(ParameterDirection.Input, input.Direction); Assert.Equal(ParameterDirection.InputOutput, inout.Direction); Assert.Equal(ParameterDirection.Output, output.Direction);
        foreach (var parameter in new[] { input, inout, output }) { Assert.Equal(OceanBaseDbType.Binary, parameter.OceanBaseDbType); Assert.Equal(0, parameter.Size); }
        var point = Convert.FromHexString("000000000101000000000000000000F03F0000000000000040");
        var oldOutput = new byte[] { 99 }; output.Value = oldOutput;
        if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
        Assert.Same(oldOutput, output.Value); Assert.Equal(0L, await db.CountAsync(connection, table, CancellationToken.None));
        var session = connection.GetOpenSession();
        for (var i = 1; i <= 4; i++)
        {
            id.Value = i; input.Value = i % 2 == 0 ? point : (object)DBNull.Value;
            inout.Value = i % 2 == 0 ? DBNull.Value : point;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            if (i % 2 == 0) { Assert.Equal(DBNull.Value, output.Value); Assert.Equal(point, Assert.IsType<byte[]>(inout.Value)); }
            else { Assert.Equal(point, Assert.IsType<byte[]>(output.Value)); Assert.Equal(DBNull.Value, inout.Value); }
            Assert.Same(session, connection.GetOpenSession());
        }
        input.Value = DBNull.Value; inout.Value = point;
        var failure = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync()) : Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
        Assert.Equal(1062, failure.ServerErrorCode); Assert.False(failure.IsExecutionOutcomeUnknown);
        Assert.Equal(DBNull.Value, output.Value); Assert.Same(point, inout.Value); Assert.Same(session, connection.GetOpenSession());
        id.Value = 5;
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        Assert.Equal(point, Assert.IsType<byte[]>(output.Value)); Assert.Equal(DBNull.Value, inout.Value); Assert.Same(session, connection.GetOpenSession());
        await using var observer = await db.OpenAsync();
        using var query = db.Command(observer, "SELECT id,g FROM " + table + " ORDER BY id");
        if (asynchronous) await query.PrepareAsync(); else query.Prepare();
        using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
        for (var i = 1; i <= 5; i++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); DatabaseContext.EqualNumber(reader.GetValue(0), i, "Geometry routine storage mismatch.");
            if (i % 2 == 0) Assert.Equal(point, reader.GetFieldValue<byte[]>(1)); else Assert.True(reader.IsDBNull(1));
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());

        var function = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        db.RegisterCleanup("FUNCTION", function, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP FUNCTION " + function, CancellationToken.None);
        });
        await db.ExecuteAsync(connection, "CREATE FUNCTION " + function + "(p GEOMETRY) RETURNS GEOMETRY DETERMINISTIC RETURN p", CancellationToken.None);
        using var call = db.Command(connection, function); call.CommandType = CommandType.StoredProcedure;
        if (asynchronous) await call.DeriveParametersAsync(); else call.DeriveParameters();
        var argument = Assert.Single(call.Parameters.Cast<OceanBaseParameter>(), p => p.Direction == ParameterDirection.Input);
        var returned = Assert.Single(call.Parameters.Cast<OceanBaseParameter>(), p => p.Direction == ParameterDirection.ReturnValue);
        Assert.Equal(OceanBaseDbType.Binary, argument.OceanBaseDbType); Assert.Equal(OceanBaseDbType.Binary, returned.OceanBaseDbType);
        if (prepared) { if (asynchronous) await call.PrepareAsync(); else call.Prepare(); }
        foreach (var value in new object[] { point, DBNull.Value, point })
        {
            argument.Value = value;
            if (asynchronous) await call.ExecuteNonQueryAsync(); else call.ExecuteNonQuery();
            if (value == DBNull.Value) Assert.Equal(DBNull.Value, returned.Value); else Assert.Equal(point, Assert.IsType<byte[]>(returned.Value));
            Assert.Same(session, connection.GetOpenSession());
        }
    }

    public static IEnumerable<object[]> FallbackCases()
    {
        foreach (var profile in IntegrationEnvironment.Profiles)
        {
            var builder = profile.CreateBuilder();
            if (!profile.Oracle && profile.CreateObjects && builder.TlsMode == OceanBaseTlsMode.Disabled && !builder.UseCompression && !builder.UseOb20 && OceanBaseEndpoint.Parse(builder.Server, builder.Port).Length == 1)
                foreach (var asynchronous in new[] { false, true }) yield return new object[] { profile.Name, asynchronous };
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(FallbackCases))]
    public async Task BinaryGeometryFallbackStoresExactBytesOnce(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var observer = await db.OpenAsync();
        var table = await db.TableAsync(observer, "id INT PRIMARY KEY,g GEOMETRY", CancellationToken.None);
        var sql = "INSERT INTO " + table + " VALUES (?,?)";
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        var endpoint = OceanBaseEndpoint.Parse(builder.Server, builder.Port).Single();
        await using var relay = new PrepareRefusalRelay(endpoint.Host, endpoint.Port, sql);
        builder.Server = "127.0.0.1"; builder.Port = relay.Port;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        using var insert = db.Command(connection, sql); insert.AllowClientSidePrepareFallback = true;
        var id = DatabaseContext.Parameter(insert, "", 0, DbType.Int32);
        var input = DatabaseContext.Parameter(insert, "", null, DbType.Binary);
        var point = Convert.FromHexString("000000000101000000000000000000F03F0000000000000040");
        var session = connection.GetOpenSession();
        for (var i = 0; i < 4; i++)
        {
            id.Value = i; input.Value = i == 0 ? null : i == 2 ? DBNull.Value : point; relay.Arm();
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            Assert.Equal(i + 1, relay.Refusals); Assert.Equal(i + 1, relay.PrepareRequests); Assert.Equal(0, relay.ForwardedPrepares);
            Assert.Same(session, connection.GetOpenSession());
        }
        id.Value = 4; input.Value = Array.Empty<byte>(); relay.Arm();
        var failure = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => insert.ExecuteNonQueryAsync()) : Assert.Throws<OceanBaseException>(() => insert.ExecuteNonQuery());
        Assert.NotEqual(0, failure.ServerErrorCode); Assert.False(failure.IsExecutionOutcomeUnknown);
        Assert.Equal(4L, await db.CountAsync(observer, table, CancellationToken.None));
        input.Value = point; relay.Arm();
        Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        Assert.Equal(6, relay.PrepareRequests); Assert.Equal(6, relay.Refusals); Assert.Equal(0, relay.ForwardedPrepares);
        Assert.Same(session, connection.GetOpenSession());
        using var query = db.Command(observer, "SELECT id,g FROM " + table + " ORDER BY id");
        if (asynchronous) await query.PrepareAsync(); else query.Prepare();
        using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); DatabaseContext.EqualNumber(reader.GetValue(0), i, "Fallback must execute once per insert.");
            if (i == 0 || i == 2) Assert.True(reader.IsDBNull(1)); else Assert.Equal(point, reader.GetFieldValue<byte[]>(1));
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }
}
