using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> PooledLobPhases()
        {
            foreach (var character in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var phase in new[] { "handle", "open", "partial", "complete", "disposed" })
                        yield return new object[] { character, asynchronous, phase };
        }

        [Theory]
        [MemberData(nameof(PooledLobPhases))]
        public async Task PersistentLobResourcesControlSameEndpointPoolReuse(bool character, bool asynchronous, string phase)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var reuse = phase == "disposed";
            var serving = Task.Run(async () =>
            {
                using var first = await listener.AcceptTcpClientAsync(stop.Token);
                var channel = await ResourceHandshake(first, stop.Token);
                await LobQueryReply(channel, character ? (byte)211 : (byte)210, InlineLob(Encoding.UTF8.GetBytes("patient-data")));
                if (reuse)
                {
                    channel.BeginCommand();
                    Assert.Equal(new byte[] { 31 }, await channel.ReadPacketAsync(stop.Token));
                    await channel.WritePacketAsync(Ok(status: 2), stop.Token);
                    await ServerIdentity.ReplyOracleAutocommitAsync(channel, stop.Token);
                    channel.BeginCommand();
                    Assert.Equal(new byte[] { 14 }, await channel.ReadPacketAsync(stop.Token));
                    await channel.WritePacketAsync(Ok(status: 2), stop.Token);
                    await ResourceReplacement(channel, first, stop.Token);
                }
                else
                {
                    Assert.Equal(0, await first.GetStream().ReadAsync(new byte[1], stop.Token));
                    using var next = await listener.AcceptTcpClientAsync(stop.Token);
                    await ResourceReplacement(await ResourceHandshake(next, stop.Token), next, stop.Token);
                }
            });
            using var connection = new OceanBaseConnection(LobConfiguration(((IPEndPoint)listener.LocalEndpoint).Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1");
            OceanBaseLob? lob = null; Stream? stream = null; TextReader? text = null;
            Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                var session = connection.GetOpenSession(); var generation = session.Generation;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select lob_value";
                    lob = Assert.IsType<OceanBaseLob>(asynchronous ? await command.ExecuteScalarAsync(stop.Token) : command.ExecuteScalar());
                }
                if (phase != "handle")
                {
                    if (character) text = lob.OpenTextReader(); else stream = lob.OpenRead();
                    if (phase == "partial")
                    {
                        if (character) Assert.Equal(1, asynchronous ? await text!.ReadAsync(new char[1], 0, 1) : text!.Read(new char[1], 0, 1));
                        else Assert.Equal(1, asynchronous ? await stream!.ReadAsync(new byte[1], 0, 1, stop.Token) : stream!.Read(new byte[1], 0, 1));
                    }
                    if (phase == "complete" || reuse)
                    {
                        if (character) Assert.Equal("patient-data", asynchronous ? await text!.ReadToEndAsync() : text!.ReadToEnd());
                        else
                        {
                            using var contents = new MemoryStream();
                            if (asynchronous) await stream!.CopyToAsync(contents, stop.Token); else stream!.CopyTo(contents);
                            Assert.Equal(Encoding.UTF8.GetBytes("patient-data"), contents.ToArray());
                        }
                    }
                }
                if (reuse) { if (asynchronous) await lob.DisposeAsync(); else lob.Dispose(); }
                Assert.Equal(!reuse, session.RequiresDiscardOnReturn);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                if (reuse) { Assert.Same(session, connection.GetOpenSession()); Assert.True(session.Generation > generation); }
                else Assert.NotSame(session, connection.GetOpenSession());
                Assert.ThrowsAny<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
                if (text != null) Assert.ThrowsAny<InvalidOperationException>(() => text.Read());
                if (stream != null) Assert.ThrowsAny<InvalidOperationException>(() => stream.Read(new byte[1], 0, 1));
                await Assert.ThrowsAnyAsync<InvalidOperationException>(() => lob.TruncateAsync(0, stop.Token));
                await Assert.ThrowsAnyAsync<InvalidOperationException>(() => character ? lob.WriteTextAsync(0, "x", stop.Token) : lob.WriteBytesAsync(0, new byte[] { 1 }, 0, 1, stop.Token));
                using (var binding = connection.CreateCommand())
                {
                    binding.CommandText = "select :lob from dual"; binding.Parameters.Add(new OceanBaseParameter { ParameterName = "lob", Value = lob });
                    Assert.ThrowsAny<InvalidOperationException>(() => binding.ExecuteScalar());
                }
                text?.Dispose(); stream?.Dispose();
                if (asynchronous) await lob.DisposeAsync(); else lob.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "replacement is usable";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync(stop.Token) : after.ExecuteNonQuery());
                OceanBaseConnection.ClearPool(connection);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                await FinishResourceTest(new Action[] { () => text?.Dispose(), () => stream?.Dispose(), () => lob?.Dispose(), connection.Close, () => OceanBaseConnection.ClearPool(connection), () => stop.Cancel(), listener.Stop }, serving, stop, failure);
            }
        }

        [Theory]
        [InlineData(false, "unopened")]
        [InlineData(true, "unopened")]
        [InlineData(false, "partial")]
        [InlineData(true, "partial")]
        [InlineData(false, "complete")]
        [InlineData(true, "complete")]
        [InlineData(false, "disposed")]
        [InlineData(true, "disposed")]
        public async Task OracleOutputCursorRetirementProtectsTheNextSameEndpointBorrower(bool asynchronous, string phase)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var first = await listener.AcceptTcpClientAsync(stop.Token);
                var channel = await ResourceHandshake(first, stop.Token);
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                if (phase == "partial" || phase == "complete")
                {
                    channel.BeginCommand();
                    Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 2, 0, 0, 0 }, await StatementCommand(channel, 28));
                    await CursorMetadata(channel, 2, false);
                    await channel.WritePacketAsync(CursorRow(1), stop.Token);
                    await channel.WritePacketAsync(CursorRow(2), stop.Token);
                    await channel.WritePacketAsync(CursorTerminator(130, false), stop.Token);
                }
                // Reader completion queues IDs; only explicit cursor disposal flushes them before Close.
                if (phase == "disposed")
                {
                    foreach (var id in new byte[] { 91, 42 })
                    {
                        channel.BeginCommand();
                        Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await channel.ReadPacketAsync(stop.Token));
                    }
                }
                await ResourceTransportClosed(first, stop.Token);
                using var next = await listener.AcceptTcpClientAsync(stop.Token);
                await ResourceReplacement(await ResourceHandshake(next, stop.Token), next, stop.Token);
            });
            using var connection = new OceanBaseConnection(LobConfiguration(((IPEndPoint)listener.LocalEndpoint).Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1");
            OceanBaseRefCursor? cursor = null; OceanBaseDataReader? reader = null; OceanBaseCommand? command = null;
            Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                var session = connection.GetOpenSession();
                command = RefCursorCall(connection);
                if (asynchronous) await command.ExecuteNonQueryAsync(stop.Token); else command.ExecuteNonQuery();
                cursor = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value); command.Dispose();
                if (phase == "partial" || phase == "complete")
                {
                    reader = asynchronous ? await cursor.GetDataReaderAsync(2, cancellationToken: stop.Token) : cursor.GetDataReader(2);
                    Assert.True(asynchronous ? await reader.ReadAsync(stop.Token) : reader.Read());
                    Assert.Equal(1L, reader.GetInt64(0));
                    if (phase == "complete")
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync(stop.Token) : reader.Read()); Assert.Equal(2L, reader.GetInt64(0));
                        Assert.False(asynchronous ? await reader.ReadAsync(stop.Token) : reader.Read());
                        Assert.False(asynchronous ? await reader.NextResultAsync(stop.Token) : reader.NextResult());
                        if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                    }
                }
                if (phase == "disposed") { if (asynchronous) await cursor.DisposeAsync(); else cursor.Dispose(); }
                Assert.True(session.RequiresDiscardOnReturn); // The opaque call retires even fully released cursor sessions.
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                Assert.NotSame(session, connection.GetOpenSession());
                Assert.ThrowsAny<InvalidOperationException>(() => cursor.GetDataReader());
                await Assert.ThrowsAnyAsync<InvalidOperationException>(() => cursor.GetDataReaderAsync(cancellationToken: stop.Token));
                if (reader != null) { if (asynchronous) await reader.DisposeAsync(); else reader.Dispose(); }
                if (asynchronous) await cursor.DisposeAsync(); else cursor.Dispose();
                command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "replacement is usable";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync(stop.Token) : after.ExecuteNonQuery());
                OceanBaseConnection.ClearPool(connection);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                await FinishResourceTest(new Action[] { () => reader?.Dispose(), () => cursor?.Dispose(), () => command?.Dispose(), connection.Close, () => OceanBaseConnection.ClearPool(connection), () => stop.Cancel(), listener.Stop }, serving, stop, failure);
            }
        }

        private static async Task<PacketChannel> ResourceHandshake(TcpClient client, CancellationToken token)
        {
            var channel = new PacketChannel(client.GetStream());
            var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
            greeting[Array.IndexOf(greeting, (byte)0, 1) + 20] |= 32;
            await channel.WritePacketAsync(greeting, token); await channel.ReadPacketAsync(token);
            await channel.WritePacketAsync(SessionVariablesOk(new NationalCharacterSet("utf8mb4", "utf8mb4", "utf8mb4").Variables("+00:00")), token);
            await ServerIdentity.ReplyAsync(channel, token: token);
            return channel;
        }

        private static async Task ResourceTransportClosed(TcpClient client, CancellationToken token)
        {
            try { Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], token)); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionReset) { }
        }

        private static async Task ResourceReplacement(PacketChannel channel, TcpClient client, CancellationToken token)
        {
            channel.BeginCommand(); await Query(channel, "replacement is usable"); await channel.WritePacketAsync(Ok(1), token);
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], token));
        }

        private static async Task FinishResourceTest(Action[] cleanups, Task serving, CancellationTokenSource stop, Exception? failure)
        {
            var errors = new List<Exception>();
            foreach (var cleanup in cleanups) { try { cleanup(); } catch (Exception error) { errors.Add(error); } }
            try { await serving; }
            catch (OperationCanceledException) when (failure != null && stop.IsCancellationRequested) { }
            catch (SocketException) when (failure != null && stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (failure != null && stop.IsCancellationRequested) { }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }
    }
}
