using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed partial class TextCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedNamedCollectionOfNestedObjectLobUsesNestedConstructors(bool asynchronous)
    {
        var server = Server(async channel =>
        {
            await ComplexDictionaryReply(channel, 72, "Owner", "Leaf_List", true,
                new[] { "Owner", "Leaf_List", "COLLECTION", null });
            await StatementCommand(channel, 25);
            var collectionMembers = await StatementCommand(channel, 22);
            Assert.Contains("ALL_COLL_TYPES", Encoding.UTF8.GetString(collectionMembers));
            await Prepared(channel, 73, 2);
            await StatementCommand(channel, 23);
            await ComplexDictionaryRows(channel, ComplexMemberNames,
                new[] { (string?)null, "1", "Owner", "Leaf", "OBJECT" });
            await StatementCommand(channel, 25);
            await ComplexDictionaryReply(channel, 74, "Owner", "Leaf", true,
                new[] { "Owner", "Leaf", "OBJECT", "2" });
            await StatementCommand(channel, 25);
            await ComplexDictionaryReply(channel, 75, "Owner", "Leaf", false,
                new[] { "A", "1", null, "BLOB", "BLOB" },
                new[] { "N", "2", null, "NUMBER", "NUMBER" });
            await StatementCommand(channel, 25);
            channel.BeginCommand();
            await LobQueryReply(channel, 210, InlineLob(new byte[] { 1, 2, 3 }));

            var prepare = await StatementCommand(channel, 22);
            Assert.Equal("BEGIN consume(\"Owner\".\"Leaf_List\"(\"Owner\".\"Leaf\"(?,?),\"Owner\".\"Leaf\"(?,?))); END;",
                Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
            await Prepared(channel, 72, 4);
            for (var i = 0; i < 2; i++)
            {
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(72, execute[1]);
                await channel.WritePacketAsync(Ok((byte)(i + 1)));
            }
            Assert.Equal(new byte[] { 25, 72, 0, 0, 0 }, await StatementCommand(channel, 25));
        }, lobLocators: true);

        using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
        try
        {
            await connection.OpenAsync();
            var type = asynchronous ? await connection.GetComplexTypeAsync("Owner", "Leaf_List") : connection.GetComplexType("Owner", "Leaf_List");
            var leaf = type.Members[0].ComplexType!;
            using var lobCommand = connection.CreateCommand();
            lobCommand.CommandText = "select lob_value";
            using var lob = (OceanBaseLob)(asynchronous ? await lobCommand.ExecuteScalarAsync() : lobCommand.ExecuteScalar())!;
            using var command = connection.CreateCommand();
            command.CommandText = "BEGIN consume(:p); END;";
            command.Parameters.Add(new OceanBaseParameter
            {
                ParameterName = "p",
                ComplexType = type,
                Value = type.CreateValue(leaf.CreateValue(lob, 7), leaf.CreateValue(lob, 8))
            });
            if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        }
        catch
        {
            if (server.Completion.IsFaulted) await server.Completion;
            throw;
        }
        finally { connection.Close(); server.Stop(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPrepareUsesNamedLobConstructorAndReusesStatement(bool asynchronous)
    {
        var server = Server(async channel =>
        {
            await ComplexScalarType(channel, collection: false, scalar: "BLOB");
            channel.BeginCommand();
            await LobQueryReply(channel, 210, InlineLob(new byte[] { 1, 2, 3 }));

            var prepare = await StatementCommand(channel, 22);
            Assert.Equal("BEGIN consume(\"Owner\".\"Thing\"(?)); END;", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
            await Prepared(channel, 71, 1);
            for (var i = 0; i < 2; i++)
            {
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(71, execute[1]);
                await channel.WritePacketAsync(Ok((byte)(i + 1)));
            }
            Assert.Equal(new byte[] { 25, 71, 0, 0, 0 }, await StatementCommand(channel, 25));
        }, lobLocators: true);

        using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
        try
        {
            await connection.OpenAsync();
            var type = asynchronous ? await connection.GetComplexTypeAsync("Owner", "Thing") : connection.GetComplexType("Owner", "Thing");
            using var lobCommand = connection.CreateCommand();
            lobCommand.CommandText = "select lob_value";
            using var lob = (OceanBaseLob)(asynchronous ? await lobCommand.ExecuteScalarAsync() : lobCommand.ExecuteScalar())!;
            using var command = connection.CreateCommand();
            command.CommandText = "BEGIN consume(:p); END;";
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = type.CreateValue(lob) });
            if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        }
        catch
        {
            if (server.Completion.IsFaulted) await server.Completion;
            throw;
        }
        finally { connection.Close(); server.Stop(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
