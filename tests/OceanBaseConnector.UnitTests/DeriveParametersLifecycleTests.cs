using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("token")]
        [InlineData("cancel")]
        [InlineData("dispose")]
        [InlineData("timeout")]
        [InlineData("membership")]
        [InlineData("context")]
        public async Task DeriveParametersKeepsOwnershipAndPreservesExistingParametersAcrossInterruption(string kind)
        {
            using var routines = DerivationRoutines(false); using var arguments = DerivationArguments(false);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = kind != "membership" && kind != "context";
            var server = Server(async channel =>
            {
                await ServeDerivationTable(channel, 71, routines, false, false, new object[] { "OWNER", "OWNER", "PROC" }, async () =>
                {
                    received.TrySetResult();
                    if (cancel) { Assert.Equal(0, await channelStreamRead(channel)); return false; }
                    await release.Task; return true;
                });
                if (!cancel)
                {
                    await ServeDerivationTable(channel, 72, arguments, false, true, new object[] { 1, "OWNER", "PROC" });
                    Assert.Equal(0, await channelStreamRead(channel));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync();
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = "owner.proc", CommandTimeout = kind == "timeout" ? 1 : 10 };
                var original = new OceanBaseParameter { ParameterName = "old", Value = 42 }; command.Parameters.Add(original);
                var pending = command.DeriveParametersAsync(cancellation.Token);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                Assert.Throws<InvalidOperationException>(() => command.Prepare());
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.DeriveParametersAsync());
                using var other = connection.CreateCommand(); other.CommandText = "select 1";
                Assert.Throws<InvalidOperationException>(() => other.ExecuteNonQuery());
                if (kind == "token") cancellation.Cancel();
                if (kind == "cancel") command.Cancel();
                if (kind == "dispose") command.Dispose();
                if (kind == "membership") { command.Parameters.Clear(); command.Parameters.Add(original); }
                if (kind == "context") command.CommandText = "another.proc";
                if (!cancel) release.TrySetResult();
                if (cancel)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.True(error.IsExecutionOutcomeUnknown);
                    if (kind == "token") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                    if (kind == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                    Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
                    Assert.Equal(ConnectionState.Open, connection.State);
                    Assert.NotEmpty(connection.GetSchema("DataTypes").Rows);
                }
                Assert.Same(original, command.Parameters[0]); Assert.Same(command.Parameters, original.Owner); Assert.Equal(42, original.Value);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task DeriveParametersEarlyGuardsDoNotReplaceParameters()
        {
            using var command = new OceanBaseCommand();
            var original = new OceanBaseParameter(); command.Parameters.Add(original);
            Assert.Throws<InvalidOperationException>(() => command.DeriveParameters());
            command.CommandType = CommandType.StoredProcedure;
            Assert.Throws<InvalidOperationException>(() => command.DeriveParameters());
            using var connection = new OceanBaseConnection(); command.Connection = connection;
            Assert.Throws<InvalidOperationException>(() => command.DeriveParameters());
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.DeriveParametersAsync(canceled.Token));
            command.Dispose(); Assert.Throws<ObjectDisposedException>(() => command.DeriveParameters());
            Assert.Same(original, command.Parameters[0]);
        }
    }
}
