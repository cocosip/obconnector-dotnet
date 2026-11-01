using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class XmlProbeTests
{
    [IntegrationFact]
    public async Task ProbeXmlTypeWire()
    {
        var profile = IntegrationEnvironment.Profiles.FirstOrDefault(p => p.Oracle && p.CreateObjects && p.HasTag("direct"));
        if (profile is null) throw new InvalidOperationException("An Oracle direct createObjects profile is required.");
        await using var db = new DatabaseContext(profile);
        await using var connection = await db.OpenAsync();
        var table = db.NewObject("TABLE");
        await db.ExecuteAsync(connection, "CREATE TABLE " + table + " (id NUMBER, x XMLTYPE)", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1, XMLTYPE('<a>患者</a>'))", CancellationToken.None);
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id, :x)"))
        {
            DatabaseContext.Parameter(insert, "id", 2);
            DatabaseContext.Parameter(insert, "x", "<b>绑定</b>", DbType.Xml);
            insert.Prepare();
            Assert.Equal(1, insert.ExecuteNonQuery());
        }
        await using (var nullInsert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id, :x)"))
        {
            DatabaseContext.Parameter(nullInsert, "id", 3);
            DatabaseContext.Parameter(nullInsert, "x", DBNull.Value, DbType.Xml);
            Assert.Equal(1, await nullInsert.ExecuteNonQueryAsync());
        }
        var function = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        db.RegisterCleanup("FUNCTION", function, async () =>
        {
            await using var cleanup = await db.OpenAsync();
            await db.ExecuteAsync(cleanup, "DROP FUNCTION " + function, CancellationToken.None);
        });
        await db.ExecuteAsync(connection,
            "CREATE FUNCTION " + function + " RETURN XMLTYPE IS BEGIN RETURN XMLTYPE('<a>患者</a>'); END;",
            CancellationToken.None);
        await using (var metadataCommand = db.Command(connection, "SELECT DISTINCT NVL(C.DATA_TYPE_OWNER, D.REFERENCED_OWNER) AS DATA_TYPE_OWNER, C.DATA_TYPE FROM ALL_TAB_COLUMNS C LEFT JOIN ALL_DEPENDENCIES D ON C.DATA_TYPE_OWNER IS NULL AND D.OWNER = C.OWNER AND D.NAME = C.TABLE_NAME AND D.TYPE = 'TABLE' AND D.REFERENCED_TYPE = 'TYPE' AND D.REFERENCED_NAME = C.DATA_TYPE AND D.REFERENCED_LINK_NAME IS NULL WHERE C.OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AND C.TABLE_NAME = '" + table + "' AND C.COLUMN_NAME = 'X'"))
        await using (var metadataReader = await metadataCommand.ExecuteReaderAsync())
        {
            while (await metadataReader.ReadAsync())
                Console.WriteLine($"XML owner lookup: {metadataReader.GetValue(0)?.ToString() ?? "NULL"} / {metadataReader.GetValue(1)?.ToString() ?? "NULL"}");
        }
        await using (var nativeCommand = db.Command(connection, "SELECT x FROM " + table + " ORDER BY id"))
        {
            await using var nativeReader = await nativeCommand.ExecuteReaderAsync();
            var schema = nativeReader.GetColumnSchema()[0];
            Assert.Equal("XMLTYPE", schema.DataTypeName);
            Assert.Equal(typeof(string), schema.DataType);
            Assert.Equal(typeof(string), nativeReader.GetProviderSpecificFieldType(0));
            Assert.Equal(1, nativeReader.FieldCount);
            Assert.True(await nativeReader.ReadAsync());
            Assert.Equal("<a>患者</a>\n", nativeReader.GetString(0));
            Assert.True(await nativeReader.ReadAsync());
            Assert.Equal("<b>绑定</b>\n", nativeReader.GetString(0));
            Assert.True(await nativeReader.ReadAsync());
            Assert.True(nativeReader.IsDBNull(0));
            Assert.False(await nativeReader.ReadAsync());
        }
        await connection.DisposeAsync();
        await using var functionConnection = await db.OpenAsync();
        await using (var functionCommand = db.Command(functionConnection, "SELECT " + function + "() FROM DUAL"))
        {
            await using var functionReader = await functionCommand.ExecuteReaderAsync();
            var schema = functionReader.GetColumnSchema()[0];
            Assert.Equal("XMLTYPE", schema.DataTypeName);
            Assert.True(await functionReader.ReadAsync());
            Assert.Equal("<a>患者</a>\n", functionReader.GetString(0));
        }
        await functionConnection.DisposeAsync();
        await using var serializedConnection = await db.OpenAsync();
        await using var command = db.Command(serializedConnection, "SELECT XMLSERIALIZE(DOCUMENT x AS CLOB) FROM " + table);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("CLOB", reader.GetDataTypeName(0));
        Assert.Equal(typeof(OceanBaseLob), reader.GetFieldType(0));
        Assert.IsType<OceanBaseLob>(reader.GetValue(0));
        Console.WriteLine($"XML serialized: {reader.GetDataTypeName(0)} / {reader.GetFieldType(0)} / {reader.GetValue(0)?.GetType().FullName ?? "NULL"}");
        while (await reader.ReadAsync()) { }
    }

    [IntegrationFact]
    public void ProbeXmlTypeWireSync()
    {
        var profile = IntegrationEnvironment.Profiles.FirstOrDefault(p => p.Oracle && p.CreateObjects && p.HasTag("direct"));
        if (profile is null) throw new InvalidOperationException("An Oracle direct createObjects profile is required.");
        var db = new DatabaseContext(profile);
        try
        {
            using var connection = db.CreateConnection();
            connection.Open();
            var table = db.NewObject("TABLE");
            using (var create = db.Command(connection, "CREATE TABLE " + table + " (id NUMBER, x XMLTYPE)"))
                create.ExecuteNonQuery();
            using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id, :x)"))
            {
                DatabaseContext.Parameter(insert, "id", 1);
                DatabaseContext.Parameter(insert, "x", "<sync>同步</sync>", DbType.Xml);
                insert.Prepare();
                Assert.Equal(1, insert.ExecuteNonQuery());
            }
            using (var insertNull = db.Command(connection, "INSERT INTO " + table + " VALUES (:id, :x)"))
            {
                DatabaseContext.Parameter(insertNull, "id", 2);
                DatabaseContext.Parameter(insertNull, "x", DBNull.Value, DbType.Xml);
                Assert.Equal(1, insertNull.ExecuteNonQuery());
            }
            using var query = db.Command(connection, "SELECT x FROM " + table + " ORDER BY id");
            using var reader = query.ExecuteReader();
            Assert.Equal("XMLTYPE", reader.GetDataTypeName(0));
            Assert.Equal(typeof(string), reader.GetFieldType(0));
            Assert.True(reader.Read());
            Assert.Equal("<sync>同步</sync>\n", reader.GetString(0));
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.False(reader.Read());
        }
        finally
        {
            db.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
