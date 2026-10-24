using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class LocalInfileTests
    {
        [Fact]
        public async Task LocalInfileIsDisabledByDefaultAndDoesNotAdvertiseCapability()
        {
            var server = LocalInfileServer(async channel =>
            {
                await Query(channel, "load data local infile 'upload.csv'");
                await channel.WritePacketAsync(LocalInfile("upload.csv"));
                try { await channel.ReadPacketAsync(); } catch (Exception) { }
            }, expectedCapability: false);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "load data local infile 'upload.csv'";
                var error = Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
                Assert.True(error.IsExecutionOutcomeUnknown);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void LocalInfileOptionRoundTripsAndNegotiatesCapability()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=one;User ID=u;Allow Local Infile=true");
            Assert.True(builder.AllowLocalInfile);
            var options = builder.Build();
            Assert.True(options.AllowLocalInfile);
            Assert.True(ClientCapabilities.Build(CapabilityFlags.ClientLocalFiles, options).HasFlag(CapabilityFlags.ClientLocalFiles));
        }

        [Fact]
        public async Task LocalInfileUploadsSyncStreamAndReadsFinalOk()
        {
            var expected = Encoding.UTF8.GetBytes("alpha\nβeta\n");
            var server = LocalInfileServer(async channel =>
            {
                await Query(channel, "load data local infile 'server-name.csv'");
                await channel.WritePacketAsync(LocalInfile("server-name.csv"));
                Assert.Equal(expected, await ReadUpload(channel));
                await channel.WritePacketAsync(Ok(2));
                channel.BeginCommand();
                await Query(channel, "after");
                await channel.WritePacketAsync(Ok(3));
            }, expectedCapability: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Local Infile=true");
            var seenName = string.Empty;
            connection.LocalInfileStreamFactory = name =>
            {
                seenName = name;
                return new MemoryStream(expected, writable: false);
            };
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "load data local infile 'server-name.csv'";
                Assert.Equal(2, command.ExecuteNonQuery());
                Assert.Equal("server-name.csv", seenName);
                command.CommandText = "after";
                Assert.Equal(3, command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalInfileUploadsAsyncStreamAndReadsFinalOk()
        {
            var expected = Encoding.UTF8.GetBytes("async-upload");
            var server = LocalInfileServer(async channel =>
            {
                await Query(channel, "load data local infile 'async.csv'");
                await channel.WritePacketAsync(LocalInfile("async.csv"));
                Assert.Equal(expected, await ReadUpload(channel));
                await channel.WritePacketAsync(Ok(1));
            }, expectedCapability: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Local Infile=true");
            connection.LocalInfileStreamFactory = _ => new MemoryStream(expected, writable: false);
            try
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "load data local infile 'async.csv'";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { await connection.CloseAsync(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalInfileMissingFactoryIsRejectedAsUnknownAndBreaksSession()
        {
            var server = LocalInfileServer(async channel =>
            {
                await Query(channel, "load data local infile 'missing.csv'");
                await channel.WritePacketAsync(LocalInfile("missing.csv"));
                try { await channel.ReadPacketAsync(); } catch (Exception) { }
            }, expectedCapability: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Local Infile=true");
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "load data local infile 'missing.csv'";
                var error = Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Contains("LOCAL INFILE stream factory", error.InnerException?.Message ?? error.Message);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalInfileNullFactoryIsRejectedAsUnknownAndBreaksSession()
        {
            var server = LocalInfileServer(async channel =>
            {
                await Query(channel, "load data local infile 'null.csv'");
                await channel.WritePacketAsync(LocalInfile("null.csv"));
                try { await channel.ReadPacketAsync(); } catch (Exception) { }
            }, expectedCapability: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Local Infile=true");
            connection.LocalInfileStreamFactory = _ => null;
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "load data local infile 'null.csv'";
                var error = Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Contains("returned null", error.InnerException?.Message ?? error.Message);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalInfileReadFailureIsUnknownAndBreaksSession()
        {
            var server = LocalInfileServer(async channel =>
            {
                await Query(channel, "load data local infile 'broken.csv'");
                await channel.WritePacketAsync(LocalInfile("broken.csv"));
                try { await channel.ReadPacketAsync(); } catch (Exception) { }
            }, expectedCapability: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Local Infile=true");
            connection.LocalInfileStreamFactory = _ => new ThrowingReadStream();
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "load data local infile 'broken.csv'";
                var error = Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalInfileCancellationIsUnknownAndBreaksSession()
        {
            var factoryCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LocalInfileServer(async channel =>
            {
                await Query(channel, "load data local infile 'cancel.csv'");
                await channel.WritePacketAsync(LocalInfile("cancel.csv"));
                try { await channel.ReadPacketAsync(); } catch (Exception) { }
            }, expectedCapability: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Local Infile=true");
            connection.LocalInfileStreamFactory = _ =>
            {
                factoryCalled.TrySetResult(true);
                return new BlockingReadStream();
            };
            try
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "load data local infile 'cancel.csv'";
                using var cancellation = new CancellationTokenSource();
                var execution = command.ExecuteNonQueryAsync(cancellation.Token);
                await factoryCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => execution);
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { await connection.CloseAsync(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static string Configuration(int port) => $"Server=127.0.0.1;Port={port};User ID=u;Password=secret;TLS Mode=Disabled;Connection Timeout=5";

        private static LoopbackServer LocalInfileServer(Func<PacketChannel, Task> exchange, bool expectedCapability) => LoopbackServer.Start(async client =>
        {
            var channel = new PacketChannel(client.GetStream());
            var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
            var versionEnd = Array.IndexOf(greeting, (byte)0, 1);
            var capabilityLower = versionEnd + 1 + 4 + 8 + 1;
            greeting[capabilityLower] |= (byte)CapabilityFlags.ClientLocalFiles;
            await channel.WritePacketAsync(greeting);
            var authentication = await channel.ReadPacketAsync();
            var capabilities = (CapabilityFlags)BitConverter.ToUInt32(authentication, 0);
            Assert.Equal(expectedCapability, capabilities.HasFlag(CapabilityFlags.ClientLocalFiles));
            await channel.WritePacketAsync(Ok(status: 2));
            await ServerIdentity.ReplyAsync(channel, oracle: false);
            await exchange(channel);
        });

        private static async Task Query(PacketChannel channel, string expected)
        {
            var request = await channel.ReadPacketAsync();
            Assert.Equal(3, request[0]);
            Assert.Equal(expected, Encoding.UTF8.GetString(request, 1, request.Length - 1));
        }

        private static byte[] LocalInfile(string filename) => new[] { (byte)251 }.Concat(Encoding.UTF8.GetBytes(filename)).ToArray();

        private static byte[] Ok(byte affected = 0, byte status = 6) => new byte[] { 0, affected, 0, status, 0, 0, 0 };

        private static async Task<byte[]> ReadUpload(PacketChannel channel)
        {
            var result = new List<byte>();
            while (true)
            {
                var packet = await channel.ReadPacketAsync();
                if (packet.Length == 0) return result.ToArray();
                result.AddRange(packet);
            }
        }

        private sealed class ThrowingReadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("upload read failed");
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(new IOException("upload read failed"));
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class BlockingReadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
