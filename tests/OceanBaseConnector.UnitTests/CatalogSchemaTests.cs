using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> CatalogMatrix()
        {
            foreach (var name in CatalogSamples.Names)
                foreach (var mysql in new[] { false, true })
                    if (!mysql || name != "Types" && name != "Synonyms")
                        foreach (var asynchronous in new[] { false, true }) yield return new object[] { name, mysql, asynchronous };
        }

        private static byte[][] CatalogPackets(string collection, bool mysql, bool binary = false, string? fault = null)
        {
            var (names, values) = CatalogSamples.Row(collection, mysql);
            if (fault == "name") names[0] = "WRONG";
            if (fault == "number") values[4] = "not a number";
            if (fault == "overflow") values[6] = "2147483648";
            if (fault == "fraction") values[6] = "1.00000000000000000000000000001";
            if (fault == "prefix") values[8] = "4294967295";
            using var row = new MemoryStream();
            if (binary)
            {
                row.WriteByte(0); var nulls = new byte[(names.Length + 9) / 8];
                for (var i = 0; i < values.Length; i++) if (values[i] == null) nulls[(i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
                row.Write(nulls);
            }
            foreach (var value in values)
            {
                if (value == null) { if (!binary) row.WriteByte(251); continue; }
                var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes);
            }
            return new[] { new[] { (byte)names.Length } }.Concat(names.Select(x => Column(x, 253))).Concat(new[] { Eof(), row.ToArray(), Eof() }).ToArray();
        }

        [Theory]
        [MemberData(nameof(CatalogMatrix))]
        public async Task CatalogDirectoriesPreserveIdentityShapeAndExactNumbers(string collection, bool mysql, bool asynchronous)
        {
            var server = Server(async channel =>
            {
                var request = await StatementCommand(channel, 3); var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                Assert.Contains(CatalogSamples.Source(collection, mysql), sql);
                if (collection == "ForeignKeyColumns" && !mysql) Assert.Contains("fk.POSITION = pk.POSITION", sql);
                if (collection == "Sequences")
                {
                    Assert.DoesNotContain("NEXTVAL", sql); Assert.DoesNotContain("CURRVAL", sql);
                    if (!mysql) Assert.Contains(" AS \"INCREMENT\"", sql);
                }
                foreach (var packet in CatalogPackets(collection, mysql)) await channel.WritePacketAsync(packet);
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var table = asynchronous ? await connection.GetSchemaAsync(collection.ToLowerInvariant()) : connection.GetSchema(collection.ToLowerInvariant());
                CatalogSamples.AssertRow(table, collection, mysql);
                using var command = connection.CreateCommand(); command.CommandText = "after";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [MemberData(nameof(CatalogMatrix))]
        public async Task CatalogRestrictionsAreBoundExactlyAndNotSql(string collection, bool mysql, bool asynchronous)
        {
            const string hostile = "Mixed_%' OR 1=1 --目标";
            var restrictions = collection == "Users" ? mysql ? new[] { hostile, "%" } : new[] { hostile, null } : mysql ? new[] { "Db目标", null, hostile } : new[] { null, "Owner目标", hostile };
            if (collection == "Indexes" || collection == "Keys" || collection == "Constraints" || collection == "ForeignKeys" || collection == "Types") restrictions = restrictions.Append(hostile).ToArray();
            if (collection == "IndexColumns" || collection == "ConstraintColumns" || collection == "ForeignKeyColumns" || collection == "Keys" || collection == "Constraints") restrictions = restrictions.Concat(collection == "Keys" || collection == "Constraints" ? new[] { "UNIQUE" } : new[] { hostile, hostile }).ToArray();
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22); var sql = Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1);
                Assert.DoesNotContain(hostile, sql); Assert.Contains(" = ?", sql);
                var values = restrictions.Where(x => x != null).Select(x => new OceanBaseParameter { Value = x, DbType = DbType.String }).ToArray();
                await Prepared(channel, 81, checked((ushort)values.Length));
                Assert.Equal(PreparedParameters.Execute(81, PreparedParameters.Encode(values, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle)), await StatementCommand(channel, 23));
                foreach (var packet in CatalogPackets(collection, mysql, true)) await channel.WritePacketAsync(packet);
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var table = asynchronous ? await connection.GetSchemaAsync(collection, restrictions) : connection.GetSchema(collection, restrictions);
                CatalogSamples.AssertRow(table, collection, mysql);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CatalogDescriptorsAndImpossibleIdentityPartsDoNotUseNetwork(bool mysql)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(Array.Empty<byte>()); InstallFaultStream(connection, transport);
                using var meta = connection.GetSchema(); using var restrictions = await connection.GetSchemaAsync("Restrictions");
                Assert.Equal(mysql ? 18 : 20, meta.Rows.Count);
                foreach (DataRow collection in meta.Rows)
                {
                    var name = (string)collection["CollectionName"];
                    var rows = restrictions.Rows.Cast<DataRow>().Where(x => (string)x["CollectionName"] == name).ToArray();
                    Assert.Equal((int)collection["NumberOfRestrictions"], rows.Length);
                    Assert.Equal(Enumerable.Range(1, rows.Length), rows.Select(x => (int)x["RestrictionNumber"]));
                    Assert.All(rows, row => Assert.Equal(DBNull.Value, row["RestrictionDefault"]));
                    Assert.Throws<ArgumentException>(() => connection.GetSchema(name, new string[rows.Length + 1]));
                    if (!CatalogSamples.Names.Contains(name)) continue;
                    if (name == "Users") { if (mysql) continue; Assert.Empty(connection.GetSchema(name, new[] { null, "host" }).Rows); }
                    else
                    {
                        var absent = mysql ? new[] { null, "" } : new[] { "" };
                        Assert.Empty((await connection.GetSchemaAsync(name, absent)).Rows);
                    }
                }
                if (mysql) foreach (var name in new[] { "Types", "Synonyms" }) Assert.Throws<NotSupportedException>(() => connection.GetSchema(name));
                Assert.Empty(transport.Written);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("Sequences", "number")]
        [InlineData("Sequences", "name")]
        [InlineData("IndexColumns", "overflow")]
        [InlineData("ForeignKeyColumns", "fraction")]
        public async Task CatalogInvalidResultsDiscardUnreadSessions(string collection, string fault)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var input = new MemoryStream(); byte sequence = 1;
                foreach (var packet in CatalogPackets(collection, false, fault: fault)) input.Write(ResponseFrame(packet, sequence++));
                InstallFaultStream(connection, new ProtocolFaultStream(input.ToArray()));
                var error = await Record.ExceptionAsync(() => connection.GetSchemaAsync(collection));
                Assert.NotNull(error); Assert.True(error is NotSupportedException || error is FormatException || error is OverflowException || error is InvalidCastException);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task CatalogErrorsStayKnownAndEmptyDirectoriesRetainShape(bool mysql, bool asynchronous)
        {
            var collections = CatalogSamples.Names.Where(x => !mysql || x != "Types" && x != "Synonyms").ToArray();
            var server = Server(async channel =>
            {
                foreach (var name in collections)
                {
                    await StatementCommand(channel, 3); await channel.WritePacketAsync(new byte[] { 255, 118, 4, 35, 52, 50, 48, 48, 48 });
                    await StatementCommand(channel, 3);
                    var packets = CatalogPackets(name, mysql);
                    foreach (var packet in packets.Where((_, i) => i != packets.Length - 2)) await channel.WritePacketAsync(packet);
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                foreach (var collection in collections)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.GetSchemaAsync(collection) : Task.FromResult(connection.GetSchema(collection)));
                    Assert.Equal(1142, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                    using var table = asynchronous ? await connection.GetSchemaAsync(collection) : connection.GetSchema(collection);
                    Assert.Equal(CatalogSamples.Row(collection, mysql).Names, table.Columns.Cast<DataColumn>().Select(x => x.ColumnName));
                    Assert.Empty(table.Rows); Assert.Equal(ConnectionState.Open, connection.State);
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task CatalogIndexPrefixLengthIsInt64()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 3);
                foreach (var packet in CatalogPackets("IndexColumns", true, fault: "prefix")) await channel.WritePacketAsync(packet);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var table = await connection.GetSchemaAsync("IndexColumns");
                Assert.Equal(4294967295L, table.Rows[0]["PREFIX_LENGTH"]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
