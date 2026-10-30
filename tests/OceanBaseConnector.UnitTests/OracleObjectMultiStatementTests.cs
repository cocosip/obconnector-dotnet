using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed partial class TextCommandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ObjectScriptRejectsDdlAndStaleTypesBeforeConsumingSources(bool asynchronous, bool nested)
    {
        var server = Server(async channel =>
        {
            if (nested)
            {
                await ComplexDictionaryReply(channel, 71, "Owner", "Thing", true, new[] { "Owner", "Thing", "OBJECT", "1" });
                await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 72, "Owner", "Thing", false, new[] { "CHILD", "1", "Owner", "Child", "OBJECT" });
                await StatementCommand(channel, 25);
                await ComplexScalarType(channel, name: "Child", collection: false);
            }
            else await ComplexScalarType(channel, collection: false);
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
            var value = nested ? type.CreateValue(type.Members[0].ComplexType!.CreateValue(new byte[] { 42 })) : type.CreateValue(new byte[] { 42 });
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = value });
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
