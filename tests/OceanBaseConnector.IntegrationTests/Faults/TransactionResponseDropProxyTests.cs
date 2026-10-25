using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Faults;

public sealed class TransactionResponseDropProxyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptBoundarySuppressesStoppedListenerOnlyAfterCancellation(bool canceled)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var stop = new CancellationTokenSource();
        if (canceled) stop.Cancel();
        if (canceled) Assert.Null(await TransactionResponseDropProxy.AcceptAsync(listener, stop.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => TransactionResponseDropProxy.AcceptAsync(listener, stop.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelaySuppressesOnlyArmedReplyAndForwardsReplacementUnchanged(bool partial)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var target = Frame(Encoding.UTF8.GetBytes("\u0003COMMIT"), 0);
        var response = Frame(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, 1);
        var sameTextOtherSequence = Frame(Encoding.UTF8.GetBytes("\u0003COMMIT"), 1);
        var serving = Task.Run(async () =>
        {
            using (var first = await upstream.AcceptTcpClientAsync(deadline.Token))
            {
                var stream = first.GetStream();
                await EqualFrame(stream, sameTextOtherSequence); await stream.WriteAsync(response, deadline.Token);
                await EqualFrame(stream, target); await stream.WriteAsync(response, deadline.Token);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
            }
            using var second = await upstream.AcceptTcpClientAsync(deadline.Token);
            await EqualFrame(second.GetStream(), target); await second.GetStream().WriteAsync(response, deadline.Token);
            Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], deadline.Token));
        });
        var proxy = new TransactionResponseDropProxy("127.0.0.1", ((IPEndPoint)upstream.LocalEndpoint).Port, "COMMIT", partial);
        TcpClient? client = null; Exception? failure = null;
        try
        {
            client = new TcpClient(); await client.ConnectAsync("127.0.0.1", proxy.Port, deadline.Token);
            proxy.Arm();
            await client.GetStream().WriteAsync(sameTextOtherSequence, deadline.Token);
            await EqualFrame(client.GetStream(), response); Assert.Equal(0, proxy.TargetRequests);
            await client.GetStream().WriteAsync(target, deadline.Token);
            using var received = new MemoryStream(); await client.GetStream().CopyToAsync(received, deadline.Token);
            Assert.Equal(partial ? response.AsSpan(0, 5).ToArray() : Array.Empty<byte>(), received.ToArray());
            Assert.Equal((byte)0, proxy.ObservedResponseMarker); Assert.Equal(1, proxy.TargetRequests);
            client.Dispose(); client = new TcpClient(); await client.ConnectAsync("127.0.0.1", proxy.Port, deadline.Token);
            await client.GetStream().WriteAsync(target, deadline.Token); await EqualFrame(client.GetStream(), response);
            Assert.Equal(2, proxy.TargetRequests); client.Dispose(); client = null;
            await serving.WaitAsync(deadline.Token); await proxy.Completion.WaitAsync(deadline.Token);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var errors = new List<Exception>(); client?.Dispose();
            try { await proxy.DisposeAsync(); } catch (Exception error) { errors.Add(error); }
            deadline.Cancel(); upstream.Stop();
            try { await serving; }
            catch (OperationCanceledException) when (failure != null) { }
            catch (SocketException) when (failure != null) { }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }

        async Task EqualFrame(Stream stream, byte[] expected)
        { var bytes = new byte[expected.Length]; await stream.ReadExactlyAsync(bytes, deadline.Token); Assert.Equal(expected, bytes); }
        static byte[] Frame(byte[] payload, byte sequence)
        { var frame = new byte[payload.Length + 4]; frame[0] = (byte)payload.Length; frame[3] = sequence; payload.CopyTo(frame, 4); return frame; }
    }
}
