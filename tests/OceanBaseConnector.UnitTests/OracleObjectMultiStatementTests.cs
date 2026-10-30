using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed partial class TextCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObjectScriptRejectsDdlAndStaleTypesBeforeConsumingSources(bool asynchronous)
    {
        var server = Server(async channel =>
        {
            await ComplexScalarType(channel, collection: false);
            channel.BeginCommand();
            await Query(channel, "after object rejection"); await channel.WritePacketAsync(Ok(1));
        }, multiStatements: true);
        using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
        try
        {
            await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
            using var source = new MemoryStream(new byte[] { 1 });
            using var command = connection.CreateCommand();
            command.CommandText = "insert into t values(:s,:p); ALTER TYPE Thing ADD ATTRIBUTE n NUMBER";
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "s", Value = source });
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = type.CreateValue(new byte[] { 42 }) });
            Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
            await Assert.ThrowsAsync<NotSupportedException>(execute); Assert.Equal(0, source.Position);
            command.CommandText = "insert into t values(:s,:p); select 1";
            connection.ClearComplexTypeCache();
            await Assert.ThrowsAsync<InvalidOperationException>(execute); Assert.Equal(0, source.Position);
            command.Parameters.Clear(); command.CommandText = "after object rejection";
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        }
        finally { connection.Close(); server.Stop(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObjectScriptDoesNotUseUnusedNationalBinaryEncoding(bool asynchronous)
    {
        var server = Server(async channel =>
        {
            await ComplexNationalDictionary(channel, "NVARCHAR2");
            channel.BeginCommand();
            await Query(channel, "insert into t values((\"Owner\".\"Thing\"(u'\\4e2d'))); select 1");
            await channel.WritePacketAsync(Ok(1, 14)); await channel.WritePacketAsync(Ok(0));
        }, multiStatements: true, nationalCharacterSet: "unsupported");
        using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
        try
        {
            await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
            using var command = connection.CreateCommand(); command.CommandText = "insert into t values(:p); select 1";
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = type.CreateValue("中") });
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        }
        finally { connection.Close(); server.Stop(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
