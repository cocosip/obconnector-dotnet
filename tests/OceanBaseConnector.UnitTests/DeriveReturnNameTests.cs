using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> ReturnNameDerivationCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var mysql in new[] { false, true })
                    foreach (var shape in new[] { "ordinary", "exact", "chain", "gap", "maximum" })
                        yield return new object[] { asynchronous, mysql, shape };
        }

        [Theory]
        [MemberData(nameof(ReturnNameDerivationCases))]
        public async Task DeriveFunctionReturnNameAvoidsFormalLabelsWithoutChangingTheirIdentity(bool asynchronous, bool mysql, string shape)
        {
            var names = shape == "ordinary" ? new[] { "amount" } : shape == "exact" ? new[] { "RETURN_VALUE" }
                : shape == "chain" ? new[] { "return_value", "Return_Value_1", "RETURN_VALUE_2" }
                : shape == "gap" ? new[] { "return_value", "RETURN_VALUE_2" }
                : Enumerable.Range(0, 1023).Select(i => i == 0 ? "RETURN_VALUE" : "RETURN_VALUE_" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
            var returnName = "RETURN_VALUE" + (shape == "ordinary" ? "" : shape == "chain" ? "_3" : shape == "maximum" ? "_1023" : "_1");
            using var routines = DerivationRoutines(mysql, "FUNCTION");
            using var arguments = DerivationArguments(mysql);
            var template = arguments.Rows[0].ItemArray; arguments.Rows.Clear();
            for (var i = names.Length; i >= 0; i--)
            {
                var row = arguments.NewRow(); row.ItemArray = template;
                row["ROUTINE_TYPE"] = "FUNCTION"; row["ORDINAL_POSITION"] = i;
                row["PARAMETER_NAME"] = i == 0 ? DBNull.Value : names[i - 1]; row["PARAMETER_MODE"] = i == 0 ? "RETURN" : "IN";
                arguments.Rows.Add(row);
            }
            using var invalid = arguments.Copy();
            if (names.Length == 1)
            {
                var duplicate = invalid.NewRow(); duplicate.ItemArray = invalid.Rows[0].ItemArray;
                duplicate["ORDINAL_POSITION"] = 2; invalid.Rows.Add(duplicate);
            }
            else invalid.Rows[0]["PARAMETER_NAME"] = names[0];
            var server = Server(async channel =>
            {
                for (byte round = 0; round < 3; round++)
                {
                    await ServeDerivationTable(channel, (byte)(71 + round * 2), routines, mysql, false,
                        mysql ? new object[] { "db", "proc" } : new object[] { "OWNER", "OWNER", "PROC" });
                    await ServeDerivationTable(channel, (byte)(72 + round * 2), round == 2 ? invalid : arguments, mysql, true,
                        mysql ? new object[] { "FUNCTION", "db", "proc" } : new object[] { 1, "OWNER", "PROC" });
                }
                Assert.Equal(new byte[] { 25, 76, 0, 0, 0 }, await StatementCommand(channel, 25));
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(7));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure,
                    CommandText = mysql ? "db.proc" : "owner.proc" };
                var original = new OceanBaseParameter { ParameterName = "old", Value = 42 }; command.Parameters.Add(original);
                for (var run = 0; run < 2; run++)
                {
                    if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
                    Assert.Equal(new[] { returnName }.Concat(names), command.Parameters.Cast<OceanBaseParameter>().Select(p => p.ParameterName));
                    Assert.Equal(ParameterDirection.ReturnValue, command.Parameters[0].Direction);
                    Assert.Same(command.Parameters[0], command.Parameters[returnName.ToLowerInvariant()]);
                    for (var i = 0; i < names.Length; i++)
                    {
                        Assert.Equal(ParameterDirection.Input, command.Parameters[i + 1].Direction);
                        Assert.Same(command.Parameters[i + 1], command.Parameters[names[i]]);
                        command.Parameters[i + 1].Value = 1m;
                    }
                    Assert.Null(original.Owner);
                    if (mysql)
                    {
                        var plan = MySqlCommandPlan.Create(command.CommandText, (OceanBaseParameterCollection)command.Parameters, "test");
                        Assert.Same(command.Parameters[0], Assert.Single(plan.Outputs));
                        Assert.Equal(names.Length, plan.Bindings.Parameters.Length);
                    }
                    else
                    {
                        var plan = OracleCommandPlan.Create(command.CommandText, command.CommandType, (OceanBaseParameterCollection)command.Parameters);
                        Assert.Same(command.Parameters[0], Assert.Single(plan.Outputs));
                        Assert.Equal(names.Length + 1, plan.Bindings.Parameters.Length);
                        Assert.DoesNotContain(returnName, plan.Bindings.CommandText);
                    }
                }
                var retained = command.Parameters.Cast<OceanBaseParameter>().ToArray(); retained[0].Value = 9m;
                if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => command.DeriveParametersAsync());
                else Assert.Throws<NotSupportedException>(() => command.DeriveParameters());
                Assert.Equal(retained, command.Parameters.Cast<OceanBaseParameter>()); Assert.Equal(9m, retained[0].Value);
                Assert.All(retained, p => Assert.Same(command.Parameters, p.Owner));
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
