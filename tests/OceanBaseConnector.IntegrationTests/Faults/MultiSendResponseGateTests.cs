using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Faults;

public sealed class MultiSendResponseGateTests
{
    [Fact]
    public async Task DisposeBeforeClientConnectsCompletesRelay()
    {
        var gate = new MultiSendResponseGate("127.0.0.1", 1);
        await gate.DisposeAsync();
        await gate.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GateForwardsFirstReplyAndWithholdsOnlyAValidSecondDmlReply(bool invalidMarker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var execute = new byte[] { 10, 0, 0, 0, 23, 90, 0, 0, 0, 0, 1, 0, 0, 0 };
        var ok = new byte[] { 7, 0, 0, 1, 0, 1, 0, 2, 0, 0, 0 };
        var greeting = new byte[] { 1, 0, 0, 0, 10 };
        var preflight = new byte[] { 1, 0, 0, 0, 3 };
        var preflightReply = new byte[] { 1, 0, 0, 1, 1 };
        var serving = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(deadline.Token); var stream = peer.GetStream();
            await stream.WriteAsync(greeting, deadline.Token);
            Assert.Equal(preflight, await TransactionResponseDropProxy.ReadFrame(stream, deadline.Token));
            await stream.WriteAsync(preflightReply, deadline.Token);
            Assert.Equal(execute, await TransactionResponseDropProxy.ReadFrame(stream, deadline.Token));
            await stream.WriteAsync(ok, deadline.Token);
            Assert.Equal(execute, await TransactionResponseDropProxy.ReadFrame(stream, deadline.Token));
            var second = (byte[])ok.Clone(); if (invalidMarker) second[4] = 255;
            await stream.WriteAsync(second, deadline.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
        });
        var gate = new MultiSendResponseGate("127.0.0.1", ((IPEndPoint)upstream.LocalEndpoint).Port);
        using var client = new TcpClient();
        Exception? failure = null; Exception? expectedFailure = null;
        try
        {
            await client.ConnectAsync("127.0.0.1", gate.Port, deadline.Token);
            var stream = client.GetStream();
            Assert.Equal(greeting, await TransactionResponseDropProxy.ReadFrame(stream, deadline.Token));
            gate.Arm(); await stream.WriteAsync(preflight, deadline.Token);
            Assert.Equal(preflightReply, await TransactionResponseDropProxy.ReadFrame(stream, deadline.Token));
            await stream.WriteAsync(execute, deadline.Token);
            await stream.WriteAsync(execute, deadline.Token);
            Assert.Equal(ok, await TransactionResponseDropProxy.ReadFrame(stream, deadline.Token));
            if (invalidMarker)
            {
                var error = await Assert.ThrowsAsync<AggregateException>(() => gate.Completion.WaitAsync(deadline.Token));
                Assert.IsType<InvalidDataException>(Assert.Single(error.Flatten().InnerExceptions)); expectedFailure = error;
                Assert.False(gate.SecondResponseHeld.IsCompleted);
            }
            else
            {
                await gate.SecondResponseHeld.WaitAsync(deadline.Token);
                client.Client.Shutdown(SocketShutdown.Send);
                Assert.Null(await TransactionResponseDropProxy.ReadFrame(stream, deadline.Token));
                await gate.Completion.WaitAsync(deadline.Token);
            }
            Assert.Equal(2, gate.Executions);
            await serving.WaitAsync(deadline.Token);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var errors = new List<Exception>(); client.Dispose();
            try { await gate.DisposeAsync(); }
            catch (Exception error) when (ReferenceEquals(error, expectedFailure)) { }
            catch (Exception error) { errors.Add(error); }
            deadline.Cancel(); upstream.Stop();
            try { await serving; }
            catch (OperationCanceledException) when (failure != null) { }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }
    }
}
