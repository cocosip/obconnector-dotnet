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
        await using (var metadataCommand = db.Command(connection, "SELECT DISTINCT NVL(C.DATA_TYPE_OWNER, D.REFERENCED_OWNER) AS DATA_TYPE_OWNER, C.DATA_TYPE FROM ALL_TAB_COLUMNS C LEFT JOIN ALL_DEPENDENCIES D ON C.DATA_TYPE_OWNER IS NULL AND D.OWNER = C.OWNER AND D.NAME = C.TABLE_NAME AND D.TYPE = 'TABLE' AND D.REFERENCED_TYPE = 'TYPE' AND D.REFERENCED_NAME = C.DATA_TYPE AND D.REFERENCED_LINK_NAME IS NULL WHERE C.OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AND C.TABLE_NAME = '" + table + "' AND C.COLUMN_NAME = 'X'"))
        await using (var metadataReader = await metadataCommand.ExecuteReaderAsync())
        {
            while (await metadataReader.ReadAsync())
                Console.WriteLine($"XML owner lookup: {metadataReader.GetValue(0)?.ToString() ?? "NULL"} / {metadataReader.GetValue(1)?.ToString() ?? "NULL"}");
        }
        OceanBaseException? nativeError = null;
        try
        {
            await using var schemaCommand = db.Command(connection, "SELECT x FROM " + table);
            await using var schemaReader = await schemaCommand.ExecuteReaderAsync(CommandBehavior.SchemaOnly);
            var schema = schemaReader.GetColumnSchema()[0];
            Console.WriteLine($"XML schema: {schema.DataTypeName} / {schema.DataType?.FullName} / {schema.ColumnName}");
        }
        catch (OceanBaseException error)
        {
            nativeError = error;
            Console.WriteLine($"XML native query rejected: {error.GetType().FullName}: {error.Message}");
        }
        await connection.DisposeAsync();
        Assert.NotNull(nativeError);
        Assert.IsType<NotSupportedException>(nativeError.InnerException);
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
}
