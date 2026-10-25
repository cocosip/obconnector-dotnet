using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private const string ComplexNationalStorageSql = "SELECT VALUE FROM NLS_DATABASE_PARAMETERS WHERE PARAMETER = 'NLS_NCHAR_CHARACTERSET'";

        private static async Task<byte[]> ComplexNationalCommand(PacketChannel channel, byte code, string national)
        {
            var request = await ReadNationalClientCommand(channel, national); Assert.Equal(code, request[0]); return request;
        }

        private static async Task ComplexNationalDictionary(PacketChannel channel, string scalar, string? storage = "AL16UTF16")
        {
            await ComplexScalarType(channel, scalar: scalar, collection: false);
            channel.BeginCommand();
            await Query(channel, ComplexNationalStorageSql);
            await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("VALUE", 253)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(storage == null ? new byte[] { 251 } : WithLength(Encoding.UTF8.GetBytes(storage)));
            await channel.WritePacketAsync(Eof());
        }

        public static IEnumerable<object[]> ComplexNationalCases()
        {
            foreach (var scalar in new[] { "NCHAR", "NVARCHAR2" })
                foreach (var national in new[] { "utf8mb4", "utf16" })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var sequential in new[] { false, true }) yield return new object[] { scalar, national, asynchronous, sequential };
        }

        [Theory]
        [MemberData(nameof(ComplexNationalCases))]
        public async Task ComplexNationalPublicPathsKeepStorageInputAndOutputEncodingsDistinct(string scalar, string national, bool asynchronous, bool sequential)
        {
            const string text = "\ufeff中文🙂  ";
            var stored = WithLength(NationalCharacterSet.Utf16.GetBytes(text));
            var output = WithLength((national == "utf16" ? NationalCharacterSet.Utf16 : ResultColumn.Utf8).GetBytes(text));
            var server = Server(async channel =>
            {
                await ComplexNationalDictionary(channel, scalar);
                await ComplexNationalCommand(channel, 22, national); await Prepared(channel, 211, 0); await ComplexNationalCommand(channel, 23, national);
                await channel.WritePacketAsync(new byte[] { 2 }); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Column("after", 8)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0 }.Concat(stored).Concat(BitConverter.GetBytes(42L)).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 0, 4 }.Concat(BitConverter.GetBytes(43L)).ToArray()); await channel.WritePacketAsync(Eof());
                await ComplexNationalCommand(channel, 25, national);
                var request = await ComplexNationalCommand(channel, 0xa1, national);
                var encoded = Convert.FromHexString("0001A000054F776E6572055468696E670000").Concat(WithLength(ResultColumn.Utf8.GetBytes(text))).ToArray();
                Assert.Equal(OraclePrepareExecutePacket.Build(0, "BEGIN pkg.p(?); END;", 1, encoded), request);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, 4)); await channel.WritePacketAsync(Column("p", 253)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0 }.Concat(output).ToArray()); await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok(1));
                await ComplexNationalCommand(channel, 25, national);
            }, serverVersion: "4.3.5.0", nationalCharacterSet: national);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync();
                var type = asynchronous ? await connection.GetComplexTypeAsync("Owner", "Thing") : connection.GetComplexType("Owner", "Thing");
                Assert.Equal(scalar, type.Members[0].TypeName); Assert.Same(type, connection.GetComplexType("Owner", "Thing"));
                var input = type.CreateValue(text);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "complex"; if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(text, Assert.Single(Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0)).GetValues())); Assert.Equal(42L, reader.GetInt64(1));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    if (!sequential) Assert.Null(Assert.Single(Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0)).GetValues()));
                    Assert.Equal(43L, reader.GetInt64(1)); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                    var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, Value = input }; command.Parameters.Add(parameter);
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    Assert.Equal(text, Assert.Single(Assert.IsType<OceanBaseComplexValue>(parameter.Value).GetValues())); Assert.Equal(text, Assert.Single(input.GetValues()));
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "UTF8")]
        [InlineData(true, "unknown")]
        [InlineData(false, null)]
        [InlineData(true, "")]
        public async Task ComplexNationalDictionaryRejectsUntrustedStorageEncodingWithoutPublishing(bool asynchronous, string? storage)
        {
            var server = Server(async channel =>
            {
                await ComplexNationalDictionary(channel, "NVARCHAR2", storage);
                channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
                await ComplexNationalDictionary(channel, "NVARCHAR2");
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? connection.GetComplexTypeAsync("Owner", "Thing") : Task.FromResult(connection.GetComplexType("Owner", "Thing")));
                using (var command = connection.CreateCommand()) { command.CommandText = "after"; Assert.Equal(7, await command.ExecuteNonQueryAsync()); }
                var valid = await connection.GetComplexTypeAsync("Owner", "Thing"); Assert.Equal("NVARCHAR2", valid.Members[0].TypeName);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "NCHAR")]
        [InlineData(true, "NCHAR")]
        [InlineData(false, "NVARCHAR2")]
        [InlineData(true, "NVARCHAR2")]
        public async Task ComplexNationalArrayExecutionUsesConnectionBytesAndKeepsTransactionUsable(bool asynchronous, string scalar)
        {
            var code = scalar == "NCHAR" ? (byte)208 : (byte)207;
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                var cursor = new ByteCursor(await ComplexNationalCommand(channel, 0xa1, "utf16"));
                Assert.Equal(0xa1, cursor.ReadUInt8()); Assert.Equal(0u, cursor.ReadUInt32()); Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(2u, cursor.ReadUInt32());
                Assert.Equal("update t set x=?", ResultColumn.Utf8.GetString(cursor.ReadLengthEncodedBytes())); Assert.Equal(1u, cursor.ReadUInt32());
                var bytes = new byte[] { 0, 1, 160, 0, 0, 0, 0, code, 2, 2 }.Concat(WithLength(ResultColumn.Utf8.GetBytes("中文🙂"))).ToArray();
                Assert.Equal(bytes, cursor.ReadBytes(bytes.Length)); Assert.Equal(128u, cursor.ReadUInt32());
                await ArrayMetadata(channel, false); await channel.WritePacketAsync(ArrayWireRow(1)); await channel.WritePacketAsync(ArrayWireRow(1));
                await channel.WritePacketAsync(Eof(5)); await channel.WritePacketAsync(Ok(2, 5));
                await StatementCommand(channel, 25); channel.BeginCommand(); await TransactionEnd(channel, false);
            }, serverVersion: "4.3.5.0", nationalCharacterSet: "utf16");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)connection.BeginTransaction();
                var type = connection.CreateArrayType(scalar, transaction); var value = type.CreateValue("中文🙂", null);
                using (var command = new OceanBaseCommand { Connection = connection, Transaction = transaction, CommandText = "update t set x=:p" })
                {
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = value });
                    var result = asynchronous ? await command.ExecuteArrayAsync(2) : command.ExecuteArray(2);
                    Assert.True(result.IsComplete); Assert.Equal(2, result.RecordsAffected);
                }
                transaction.Rollback();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(207, "utf8mb4")]
        [InlineData(208, "utf8mb4")]
        [InlineData(207, "utf16")]
        [InlineData(208, "utf16")]
        public async Task ComplexNationalInvalidOutputPreservesAllOriginalValues(byte code, string national)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(7)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType(code == 207 ? "NVARCHAR2" : "NCHAR");
                var parameters = new OceanBaseParameterCollection();
                var scalar = new OceanBaseParameter { ParameterName = "n", Direction = ParameterDirection.Output, Value = "scalar-old" };
                var complex = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, Value = "complex-old", ComplexType = type };
                parameters.Add(scalar); parameters.Add(complex);
                var plan = OracleCommandPlan.Create("pkg.p", CommandType.StoredProcedure, parameters);
                var column = ResultColumn.Parse(ComplexColumn("", "", element: code), OceanBaseCompatibilityMode.Oracle,
                    nationalCharacterSet: new NationalCharacterSet(national, "utf8mb4", "utf8mb4"));
                plan.ValidateOutputColumn(1, column);
                var invalid = national == "utf16" ? new byte[] { 1, 0, 2, 216, 0 } : new byte[] { 1, 0, 1, 255 };
                Assert.Throws<DecoderFallbackException>(() => plan.CaptureOutput(new[] { ResultColumn.Parse(Column("n", 8), OceanBaseCompatibilityMode.Oracle), column },
                    new[] { BitConverter.GetBytes(42L), invalid }));
                Assert.Equal("scalar-old", scalar.Value); Assert.Equal("complex-old", complex.Value);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(7, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(207)]
        [InlineData(208)]
        public void AnonymousNationalOrdinaryColumnsDoNotGuessTheStorageEncoding(byte code)
        {
            var column = ResultColumn.Parse(ComplexColumn("", "", element: code), OceanBaseCompatibilityMode.Oracle,
                nationalCharacterSet: new NationalCharacterSet("utf8mb4", "utf8mb4", "utf8mb4"));
            Assert.Equal(code == 207 ? "NVARCHAR2" : "NCHAR", column.ComplexType!.Members[0].TypeName);
            Assert.Throws<NotSupportedException>(() => column.DecodeBinary(new byte[] { 1, 0, 1, 65 }));
            Assert.Null(Assert.Single(Assert.IsType<OceanBaseComplexValue>(column.DecodeBinary(new byte[] { 1, 4 })).GetValues()));
        }
    }
}
