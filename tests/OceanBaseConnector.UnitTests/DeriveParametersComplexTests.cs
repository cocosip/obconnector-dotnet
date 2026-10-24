using System;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("normal")]
        [InlineData("token")]
        [InlineData("timeout")]
        public async Task DeriveParametersLoadsComplexTypesOnTheReservedSessionWithTheCommandBudget(string kind)
        {
            using var routines = DerivationRoutines(false); using var arguments = DerivationArguments(false);
            var row = arguments.Rows[0]; row["DATA_TYPE"] = "OBJECT"; row["TYPE_OWNER"] = "OWNER"; row["TYPE_NAME"] = "THING";
            row["NUMERIC_PRECISION"] = row["NUMERIC_SCALE"] = DBNull.Value;
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await ServeDerivationTable(channel, 71, routines, false, false, new object[] { "OWNER", "OWNER", "PROC" });
                await ServeDerivationTable(channel, 72, arguments, false, true, new object[] { 1, "OWNER", "PROC" });
                async Task Request(byte id, string source)
                {
                    byte[] request;
                    do { channel.BeginCommand(); request = await ReadClientCommand(channel); } while (request[0] == 25);
                    Assert.Equal(22, request[0]); Assert.Contains(source, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    await Prepared(channel, id, 2);
                    Assert.Equal(PreparedParameters.Execute(id, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = "OWNER" }, new OceanBaseParameter { Value = "THING" } }, OceanBaseCompatibilityMode.Oracle)), await StatementCommand(channel, 23));
                }
                await Request(73, "ALL_TYPES"); received.TrySetResult();
                if (kind == "normal")
                {
                    await ComplexDictionaryRows(channel, ComplexHeaderNames, new[] { "OWNER", "THING", "OBJECT", "1" });
                    await Request(74, "ALL_TYPE_ATTRS");
                    await ComplexDictionaryRows(channel, ComplexMemberNames, new[] { "A", "1", null, "NUMBER", "NUMBER" });
                }
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync();
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = "owner.proc", CommandTimeout = kind == "timeout" ? 1 : 10 };
                var original = new OceanBaseParameter { Value = 42 }; command.Parameters.Add(original);
                var pending = command.DeriveParametersAsync(cancellation.Token);
                if (kind == "normal")
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(5));
                    var parameter = Assert.IsType<OceanBaseParameter>(command.Parameters[0]);
                    Assert.Equal(OceanBaseDbType.Struct, parameter.OceanBaseDbType); Assert.NotNull(parameter.ComplexType);
                    Assert.Same(parameter.ComplexType, connection.GetComplexType("OWNER", "THING"));
                    Assert.Null(original.Owner); Assert.Equal(DBNull.Value, parameter.Value);
                }
                else
                {
                    await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (kind == "token") cancellation.Cancel();
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    if (kind == "token") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                    else Assert.IsType<TimeoutException>(error.InnerException);
                    Assert.Same(original, command.Parameters[0]); Assert.Same(command.Parameters, original.Owner);
                }
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
