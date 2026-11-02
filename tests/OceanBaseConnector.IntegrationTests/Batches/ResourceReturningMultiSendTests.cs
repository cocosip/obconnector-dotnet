using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class ResourceReturningMultiSendTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct"))
        from asynchronous in new[] { false, true }
        from kind in new[] { "blob", "clob", "object-raw", "object-blob", "object-clob" }
        select new object[] { profile.Name, asynchronous, kind };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task ResourceReturningOutputsRetainCallerOwnedValues(string profileName, bool asynchronous, string kind)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.UseOraclePrepareExecute = true; builder.UseLobLocators = true;
        builder.UseBatchMultiSend = true; builder.BatchMultiSendNumber = 2;
        await using var db = new DatabaseContext(new IntegrationProfile
        {
            Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true,
        });
        await using var connection = await db.OpenAsync();
        var complex = kind.StartsWith("object", StringComparison.Ordinal);
        var character = kind.EndsWith("clob", StringComparison.Ordinal); var raw = kind.EndsWith("raw", StringComparison.Ordinal);
        var scalar = raw ? "RAW(20)" : character ? "CLOB" : "BLOB";
        OceanBaseComplexType? type = null;
        var typeName = scalar;
        if (complex)
        {
            typeName = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + typeName + " AS OBJECT(a " + scalar + ",n NUMBER)", CancellationToken.None);
        }
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,value " + typeName, CancellationToken.None);
        for (var id = 1; id <= 3; id++)
        {
            var payload = id == 2 ? "NULL" : character ? "TO_CLOB('患者🙂" + id + "')" :
                (raw ? "HEXTORAW('" : "TO_BLOB(HEXTORAW('") + (id == 1 ? "0080FF" : "2A") + (raw ? "')" : "'))");
            var value = complex ? typeName + "(" + payload + ",7)" : payload;
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(" + id + "," + value + ")", CancellationToken.None);
        }
        if (complex)
        {
            var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
            type = await connection.GetComplexTypeAsync(owner, typeName);
        }
        var outputs = new List<OceanBaseParameter>();
        var handles = new List<(OceanBaseLob Lob, int Row)>();
        void Retain()
        {
            for (var i = 0; i < outputs.Count; i++)
            {
                var value = outputs[i].Value;
                if (complex)
                {
                    var structured = Assert.IsType<OceanBaseComplexValue>(value); Assert.Same(type, structured.Type);
                    var members = structured.GetValues(); DatabaseContext.EqualNumber(members[1], 7, "Ordinary output members must retain their values.");
                    value = members[0];
                }
                if (i == 1) { Assert.True(value == null || value == DBNull.Value); continue; }
                if (raw) Assert.Equal(i == 0 ? new byte[] { 0, 128, 255 } : new byte[] { 42 }, Assert.IsType<byte[]>(value));
                else handles.Add((Assert.IsType<OceanBaseLob>(value), i));
            }
        }
        try
        {
            using (var batch = connection.CreateBatch())
            {
                for (var id = 1; id <= 3; id++)
                {
                    var item = batch.CreateBatchCommand(); item.CommandText = "UPDATE " + table + " SET id=id WHERE id=:id RETURNING value INTO :output";
                    item.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = id });
                    var output = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = "old" };
                    if (complex) output.ComplexType = type; else output.OceanBaseDbType = character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob;
                    item.Parameters.Add(output); outputs.Add(output); batch.BatchCommands.Add(item);
                }
                if (asynchronous) await batch.PrepareAsync();
                using (var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader())
                {
                    for (var i = 0; i < 3; i++)
                    {
                        Assert.Equal(0, reader.FieldCount); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                        Assert.Equal(i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    }
                    Assert.Equal(3, reader.RecordsAffected);
                }
                Retain();
                Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery()); Retain();
                Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar()); Retain();
            }
            connection.ClearComplexTypeCache();
            foreach (var (lob, row) in handles)
            {
                if (character) { using var source = lob.OpenTextReader(); Assert.Equal("患者🙂" + (row + 1), await source.ReadToEndAsync()); }
                else
                {
                    using var source = lob.OpenRead(); using var target = new MemoryStream(); await source.CopyToAsync(target);
                    Assert.Equal(row == 0 ? new byte[] { 0, 128, 255 } : new byte[] { 42 }, target.ToArray());
                }
            }
            DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Caller connection must remain reusable.");
            await using var observer = await db.OpenAsync(); Assert.Equal(3, await db.CountAsync(observer, table, CancellationToken.None));
        }
        finally { foreach (var (lob, _) in handles) lob.Dispose(); }
    }
}
