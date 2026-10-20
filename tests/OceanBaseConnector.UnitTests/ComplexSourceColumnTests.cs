using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static byte[] SourceComplexColumn(string table = "SourceTable", string column = "Value", string database = "TableOwner", string typeName = "Thing")
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            foreach (var name in new[] { "def", database, "alias", table, "value_alias", column })
                PreparedParameters.WriteBytes(writer, Encoding.UTF8.GetBytes(name));
            var basic = Column("complex", 160);
            writer.Write(basic, basic.Length - 13, 13);
            PreparedParameters.WriteBytes(writer, Array.Empty<byte>());
            PreparedParameters.WriteBytes(writer, Encoding.UTF8.GetBytes(typeName));
            writer.Write((byte)251);
            if (typeName.Length == 0) writer.Write((byte)0);
            return stream.ToArray();
        }

        private static async Task SourceComplexPrepare(PacketChannel channel, params byte[][] columns)
        {
            await StatementCommand(channel, 22);
            await channel.WritePacketAsync(new byte[] { 0, 181, 0, 0, 0, (byte)columns.Length, 0, 0, 0, 0, 0, 0 });
            foreach (var column in columns) await channel.WritePacketAsync(column);
            await channel.WritePacketAsync(Eof());
        }

        private static async Task SourceTypeLookup(PacketChannel channel, string table = "SourceTable", string column = "Value", params string?[][] rows)
        {
            var request = await StatementCommand(channel, 22);
            var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
            Assert.Contains("ALL_TAB_COLUMNS", sql);
            Assert.Contains("OWNER = ?", sql); Assert.Contains("TABLE_NAME = ?", sql); Assert.Contains("COLUMN_NAME = ?", sql);
            Assert.DoesNotContain("TableOwner", sql); Assert.DoesNotContain(table, sql);
            await Prepared(channel, 182, 3);
            Assert.Equal(PreparedParameters.Execute(182, PreparedParameters.Encode(new[] {
                new OceanBaseParameter { Value = "TableOwner" }, new OceanBaseParameter { Value = table }, new OceanBaseParameter { Value = column }
            }, OceanBaseCompatibilityMode.Oracle)), await StatementCommand(channel, 23));
            await ComplexDictionaryRows(channel, new[] { "DATA_TYPE_OWNER", "DATA_TYPE" }, rows);
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexResultResolvesMissingOwnerFromSourceColumnOnEveryExecution(bool asynchronous, bool omittedTypeName)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "TypeOwner");
                await ComplexScalarType(channel, "OtherOwner", scalar: "NUMBER");
                await SourceComplexPrepare(channel, SourceComplexColumn(typeName: omittedTypeName ? "" : "Thing"));
                for (var i = 0; i < 2; i++)
                {
                    await SourceTypeLookup(channel, rows: new[] { new string?[] { "TypeOwner", "Thing" } });
                    await StatementCommand(channel, 25);
                    channel.BeginCommand(); var zoneQuery = await channel.ReadPacketAsync();
                    Assert.Equal(SessionTimeZoneSql, Encoding.UTF8.GetString(zoneQuery, 1, zoneQuery.Length - 1)); await TimeZoneReply(channel, "+09:00");
                    channel.BeginCommand(); var nationalQuery = await channel.ReadPacketAsync();
                    Assert.Equal(NationalCharacterSetSql, Encoding.UTF8.GetString(nationalQuery, 1, nationalQuery.Length - 1)); await NationalCharacterSetReply(channel, "utf16");
                    await StatementCommand(channel, 23);
                    await channel.WritePacketAsync(new byte[] { 3 }); await channel.WritePacketAsync(SourceComplexColumn());
                    await channel.WritePacketAsync(ValueColumnPacket(201, scale: 9)); await channel.WritePacketAsync(Column("national", 207, charset: 54)); await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0, 1, 42 }
                        .Concat(WithLength(NanosPayload(123456789, 9))).Concat(WithLength(NationalCharacterSet.Utf16.GetBytes("\u4e2d\u6587"))).ToArray());
                    await channel.WritePacketAsync(Eof());
                }
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(7)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("TypeOwner", "Thing");
                await connection.GetComplexTypeAsync("OtherOwner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "select value from source";
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                for (var i = 0; i < 2; i++)
                {
                    using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    var value = reader.GetFieldValue<OceanBaseComplexValue>(0);
                    Assert.Same(type, value.Type); Assert.Equal(new byte[] { 42 }, value.GetValues()[0]);
                    Assert.Equal("+09:00", reader.GetFieldValue<OceanBaseTimestampLocalTimeZone>(1).SessionTimeZone);
                    Assert.Equal("\u4e2d\u6587", reader.GetString(2));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "missing")] [InlineData(true, "missing")]
        [InlineData(false, "duplicate")] [InlineData(true, "duplicate")]
        [InlineData(false, "wrong-name")] [InlineData(true, "wrong-name")]
        [InlineData(false, "unloaded-owner")] [InlineData(true, "unloaded-owner")]
        [InlineData(false, "null-owner")] [InlineData(true, "null-owner")]
        [InlineData(false, "nul-owner")] [InlineData(true, "nul-owner")]
        [InlineData(false, "missing-source")] [InlineData(true, "missing-source")]
        public async Task ComplexSourceLookupRejectsUntrustedIdentityBeforeBusinessExecution(bool asynchronous, string kind)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "TypeOwner");
                await SourceComplexPrepare(channel, SourceComplexColumn(database: kind == "missing-source" ? "" : "TableOwner"));
                if (kind != "missing-source")
                {
                    var rows = kind == "missing" ? Array.Empty<string?[]>() : kind == "duplicate"
                        ? new[] { new string?[] { "TypeOwner", "Thing" }, new string?[] { "OtherOwner", "Thing" } }
                        : new[] { new string?[] { kind == "unloaded-owner" ? "OtherOwner" : kind == "null-owner" ? null : kind == "nul-owner" ? "Type\0Owner" : "TypeOwner", kind == "wrong-name" ? "Different" : "Thing" } };
                    await SourceTypeLookup(channel, rows: rows);
                    if (kind == "wrong-name" || kind == "unloaded-owner") await StatementCommand(channel, 25);
                }
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("TypeOwner", "Thing");
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "source"; await command.PrepareAsync();
                var error = await Record.ExceptionAsync(async () => { using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader(); });
                Assert.NotNull(error); Assert.False(command.HasSentSql);
                if (error is OceanBaseException databaseError) Assert.False(databaseError.IsExecutionOutcomeUnknown);
                else Assert.IsType<NotSupportedException>(error);
                connection.Close();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "database")] [InlineData(true, "database")]
        [InlineData(false, "table")] [InlineData(true, "table")]
        [InlineData(false, "column")] [InlineData(true, "column")]
        [InlineData(false, "type")] [InlineData(true, "type")]
        public async Task ComplexSourceLookupNeverAppliesDescriptionToChangedExecuteIdentity(bool asynchronous, string kind)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "TypeOwner"); await SourceComplexPrepare(channel, SourceComplexColumn());
                await SourceTypeLookup(channel, rows: new[] { new string?[] { "TypeOwner", "Thing" } }); await StatementCommand(channel, 25);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(SourceComplexColumn(kind == "table" ? "OtherTable" : "SourceTable", kind == "column" ? "OtherValue" : "Value",
                    kind == "database" ? "OtherDatabase" : "TableOwner", kind == "type" ? "OtherType" : "Thing"));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("TypeOwner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "source"; await command.PrepareAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader(); });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.IsType<NotSupportedException>(error.InnerException);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexSourceLookupCancellationAndTimeoutNeverExecuteBusinessSql(bool asynchronous, bool timeout)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "TypeOwner"); await SourceComplexPrepare(channel, SourceComplexColumn());
                await StatementCommand(channel, 22); started.SetResult();
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("TypeOwner", "Thing");
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "source"; await command.PrepareAsync(); command.CommandTimeout = timeout ? 1 : 10;
                var operation = asynchronous ? command.ExecuteReaderAsync(cancellation.Token) : Task.Run(() => command.ExecuteReaderCore(CommandBehavior.Default, cancellation.Token));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); if (!timeout) cancellation.Cancel();
                if (timeout) await Assert.ThrowsAsync<TimeoutException>(async () => { using var reader = await operation; });
                else { var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var reader = await operation; }); Assert.Equal(cancellation.Token, error.CancellationToken); }
                Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task ComplexSourceLookupSeparatesSameNamedTypesByEachSourceColumn(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "TypeOwner"); await ComplexScalarType(channel, "OtherOwner", scalar: "NUMBER");
                await SourceComplexPrepare(channel, SourceComplexColumn("LeftTable"), SourceComplexColumn("RightTable"));
                await SourceTypeLookup(channel, "LeftTable", rows: new[] { new string?[] { "TypeOwner", "Thing" } }); await StatementCommand(channel, 25);
                await SourceTypeLookup(channel, "RightTable", rows: new[] { new string?[] { "OtherOwner", "Thing" } }); await StatementCommand(channel, 25);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(SourceComplexColumn("LeftTable")); await channel.WritePacketAsync(SourceComplexColumn("RightTable")); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0, 1, 42, 1, 0, 1, 55 }); await channel.WritePacketAsync(Eof());
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var left = await connection.GetComplexTypeAsync("TypeOwner", "Thing"); var right = await connection.GetComplexTypeAsync("OtherOwner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "source"; await command.PrepareAsync();
                using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader(); Assert.True(await reader.ReadAsync());
                var leftValue = reader.GetFieldValue<OceanBaseComplexValue>(0); var rightValue = reader.GetFieldValue<OceanBaseComplexValue>(1);
                Assert.Same(left, leftValue.Type); Assert.Same(right, rightValue.Type);
                Assert.Equal(new byte[] { 42 }, leftValue.GetValues()[0]); Assert.Equal(new OceanBaseDecimal(7), rightValue.GetValues()[0]); Assert.False(await reader.ReadAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
