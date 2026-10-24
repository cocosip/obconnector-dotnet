using System;
using System.Data;
using System.Globalization;
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
        private static DataTable DerivationRoutines(bool mysql, string kind = "PROCEDURE")
        {
            var table = DeriveParametersMetadataTests.Arguments();
            while (table.Columns.Count > 7) table.Columns.RemoveAt(table.Columns.Count - 1);
            var row = table.NewRow(); row[mysql ? "ROUTINE_CATALOG" : "ROUTINE_SCHEMA"] = mysql ? "db" : "OWNER";
            row["ROUTINE_NAME"] = mysql ? "proc" : "PROC"; row["ROUTINE_TYPE"] = kind;
            if (!mysql) row["SUBPROGRAM_ID"] = 1;
            table.Rows.Add(row); return table;
        }

        private static DataTable DerivationArguments(bool mysql)
        {
            var table = DeriveParametersMetadataTests.Arguments();
            var row = table.NewRow(); row[mysql ? "ROUTINE_CATALOG" : "ROUTINE_SCHEMA"] = mysql ? "db" : "OWNER";
            row["ROUTINE_NAME"] = mysql ? "proc" : "PROC";
            if (mysql) row["ROUTINE_TYPE"] = "PROCEDURE"; else row["SUBPROGRAM_ID"] = 1;
            row["PARAMETER_NAME"] = "amount"; row["ORDINAL_POSITION"] = 1; row["PARAMETER_MODE"] = "INOUT";
            row["DATA_TYPE"] = mysql ? "decimal" : "NUMBER"; row["NUMERIC_PRECISION"] = 3; row["NUMERIC_SCALE"] = mysql ? 1 : -2;
            if (mysql) row["TYPE_DECLARATION"] = "decimal(3,1)";
            table.Rows.Add(row); return table;
        }

        private static async Task ServeDerivationTable(PacketChannel channel, byte id, DataTable table, bool mysql, bool arguments, object[] values, Func<Task<bool>>? beforeReply = null, string? package = null)
        {
            byte[] prepare;
            do { channel.BeginCommand(); prepare = await ReadClientCommand(channel); } while (prepare[0] == 25);
            Assert.Equal(22, prepare[0]);
            var sql = Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1);
            Assert.Contains(mysql ? arguments ? "INFORMATION_SCHEMA.PARAMETERS" : "INFORMATION_SCHEMA.ROUTINES" : arguments ? "ALL_ARGUMENTS" : "ALL_PROCEDURES", sql);
            Assert.DoesNotContain("LIKE", sql);
            if (arguments)
            {
                Assert.Contains(mysql ? "DTD_IDENTIFIER AS TYPE_DECLARATION" : "CHAR_LENGTH AS CHARACTER_LENGTH", sql);
                if (!mysql) { Assert.DoesNotContain("DATA_TYPE IS NOT NULL", sql); Assert.Contains(package == null ? "PACKAGE_NAME IS NULL" : "PACKAGE_NAME = ?", sql); Assert.Contains("SUBPROGRAM_ID = ?", sql); }
            }
            await Prepared(channel, id, (ushort)values.Length);
            var bound = values.Select(v => new OceanBaseParameter { Value = v }).ToArray();
            Assert.Equal(PreparedParameters.Execute(id, PreparedParameters.Encode(bound, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle)), await StatementCommand(channel, 23));
            if (beforeReply != null && !await beforeReply()) return;
            await SendDerivationRows(channel, table);
        }

        private static async Task SendDerivationRows(PacketChannel channel, DataTable table)
        {
            await channel.WritePacketAsync(new[] { (byte)table.Columns.Count });
            foreach (DataColumn column in table.Columns) await channel.WritePacketAsync(Column(column.ColumnName, 253));
            await channel.WritePacketAsync(Eof());
            foreach (DataRow row in table.Rows)
            {
                using var packet = new MemoryStream(); packet.WriteByte(0);
                var nulls = new byte[(table.Columns.Count + 9) / 8];
                for (var i = 0; i < table.Columns.Count; i++) if (row.IsNull(i)) nulls[(i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
                packet.Write(nulls);
                foreach (var value in row.ItemArray)
                {
                    if (value == DBNull.Value) continue;
                    var content = Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                    Assert.True(content.Length < 251); packet.WriteByte((byte)content.Length); packet.Write(content);
                }
                await channel.WritePacketAsync(packet.ToArray());
            }
            await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task DeriveParametersReadsCompleteDirectoriesWithoutExecutingTheRoutine(bool asynchronous, bool mysql, bool noArguments)
        {
            using var routines = DerivationRoutines(mysql); using var arguments = DerivationArguments(mysql);
            if (noArguments) arguments.Rows.Clear();
            var server = Server(async channel =>
            {
                await ServeDerivationTable(channel, 71, routines, mysql, false, mysql ? new object[] { "db", "proc" } : new object[] { "OWNER", "OWNER", "PROC" });
                await ServeDerivationTable(channel, 72, arguments, mysql, true, mysql ? new object[] { "PROCEDURE", "db", "proc" } : new object[] { 1, "OWNER", "PROC" });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = mysql ? "db.proc" : "owner.proc" };
                var original = new OceanBaseParameter { ParameterName = "old", Value = 42 }; command.Parameters.Add(original);
                if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
                Assert.Null(original.Owner);
                if (noArguments) Assert.Empty(command.Parameters.Cast<OceanBaseParameter>());
                else
                {
                    var parameter = Assert.IsType<OceanBaseParameter>(command.Parameters[0]);
                    Assert.Equal("amount", parameter.ParameterName); Assert.Equal(ParameterDirection.InputOutput, parameter.Direction);
                    Assert.Equal(3, parameter.Precision); Assert.Equal(mysql ? 1 : -2, parameter.DecimalScale); Assert.Equal(DBNull.Value, parameter.Value);
                }
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("ambiguous")]
        [InlineData("null-type")]
        [InlineData("position")]
        [InlineData("identity")]
        [InlineData("unsupported")]
        [InlineData("partial-owner")]
        [InlineData("partial-name")]
        public async Task DeriveParametersRejectsIncompleteMetadataWithoutReplacingExistingParameters(string fault)
        {
            using var routines = DerivationRoutines(false); using var arguments = DerivationArguments(false);
            if (fault == "missing") routines.Rows.Clear();
            if (fault == "ambiguous") { var second = (object[])routines.Rows[0].ItemArray.Clone(); second[5] = 2; routines.Rows.Add(second); }
            if (fault == "null-type") arguments.Rows[0]["DATA_TYPE"] = DBNull.Value;
            if (fault == "position") arguments.Rows[0]["ORDINAL_POSITION"] = 2;
            if (fault == "identity") arguments.Rows[0]["SUBPROGRAM_ID"] = 2;
            if (fault == "unsupported") arguments.Rows[0]["DATA_TYPE"] = "UNSUPPORTED";
            if (fault == "partial-owner") arguments.Rows[0]["TYPE_OWNER"] = "OWNER";
            if (fault == "partial-name") arguments.Rows[0]["TYPE_NAME"] = "THING";
            var server = Server(async channel =>
            {
                await ServeDerivationTable(channel, 71, routines, false, false, new object[] { "OWNER", "OWNER", "PROC" });
                if (fault != "missing" && fault != "ambiguous") await ServeDerivationTable(channel, 72, arguments, false, true, new object[] { 1, "OWNER", "PROC" });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = "owner.proc" };
                var original = new OceanBaseParameter { ParameterName = "old", Value = 42 }; command.Parameters.Add(original);
                await Assert.ThrowsAsync<NotSupportedException>(() => command.DeriveParametersAsync());
                Assert.Same(original, command.Parameters[0]); Assert.Same(command.Parameters, original.Owner); Assert.Equal(42, original.Value);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
