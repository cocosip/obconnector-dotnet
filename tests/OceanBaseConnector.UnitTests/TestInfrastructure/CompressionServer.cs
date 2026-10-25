using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    // Independent framework-zlib wire peer; local transport evidence, not OceanBase interoperability.
    internal sealed class CompressionServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        private readonly Task _serving;
        private TcpClient? _peer;
        internal CompressionServer(byte[] greeting, bool oracle, bool tls = false, bool authSwitch = false, bool advertise = true, bool localInfile = false)
        {
            if (tls) Certificate = TestCertificateFactory.CreateSelfSigned();
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serving = Serve(greeting, oracle, tls, authSwitch, advertise, localInfile);
        }
        internal int Port { get; }
        internal X509Certificate2? Certificate { get; }
        internal int Authentications { get; private set; }
        internal int Queries { get; private set; }
        internal int Resets { get; private set; }
        internal int Pings { get; private set; }
        internal bool Compressed { get; private set; }
        internal long UploadedBytes { get; private set; }
        private async Task Serve(byte[] greeting, bool oracle, bool tls, bool authSwitch, bool advertise, bool localInfile)
        {
            var token = _budget.Token;
            try
            {
                using var peer = _peer = await _listener.AcceptTcpClientAsync(token); peer.NoDelay = true;
                Stream transport = peer.GetStream();
                using var secure = tls ? new SslStream(transport) : null;
                var channel = new PacketChannel(transport);
                greeting = (byte[])greeting.Clone();
                var lower = Array.IndexOf(greeting, (byte)0, 1) + 1 + 4 + 8 + 1;
                if (advertise) greeting[lower] |= 32; else greeting[lower] &= 223;
                if (localInfile) greeting[lower] |= 128;
                await channel.WritePacketAsync(greeting, token);
                if (tls)
                {
                    Assert.Equal(32, (await channel.ReadPacketAsync(token)).Length);
                    await secure!.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, token);
                    channel.ReplaceUnderlyingStream(secure); transport = secure;
                }
                var authentication = await channel.ReadPacketAsync(token); Authentications++;
                Compressed = (authentication[0] & 32) != 0;
                if (authSwitch)
                {
                    using var challenge = new MemoryStream(); challenge.WriteByte(254);
                    challenge.Write(Encoding.ASCII.GetBytes("mysql_native_password\0abcdefghijklmnopqrst\0"));
                    await channel.WritePacketAsync(challenge.ToArray(), token);
                    Assert.Equal(20, (await channel.ReadPacketAsync(token)).Length);
                }
                await channel.WritePacketAsync(SessionOk(oracle), token);
                using var compressed = Compressed ? new WireStream(transport) : null;
                if (compressed != null) channel = new PacketChannel(compressed);
                await ServerIdentity.ReplyAsync(channel, oracle, token: token, initializeAutocommit: false);
                compressed?.Reset();
                if (oracle) await ServerIdentity.ReplyOracleAutocommitAsync(channel, token);
                compressed?.Reset();
                while (true)
                {
                    var request = await channel.ReadPacketAsync(token);
                    Assert.Contains(request[0], new byte[] { 3, 0x1f, 0x0e, 1 });
                    if (request[0] == 1) break;
                    if (request[0] == 3) Queries++;
                    if (request[0] == 0x1f) Resets++;
                    if (request[0] == 0x0e) Pings++;
                    if (request[0] == 3 && localInfile)
                    {
                        await channel.WritePacketAsync(new byte[] { 251, (byte)'x' }, token);
                        while (true)
                        {
                            var data = await channel.ReadPacketAsync(token);
                            if (data.Length == 0) break;
                            foreach (var value in data) Assert.Equal((byte)42, value);
                            UploadedBytes += data.Length;
                        }
                    }
                    await channel.WritePacketAsync(request[0] == 3 ? new byte[] { 0, 1, 0, 2, 0, 0, 0 } : SessionOk(oracle), token);
                    channel.BeginCommand(); compressed?.Reset();
                    if (oracle && request[0] == 0x1f)
                    {
                        await ServerIdentity.ReplyOracleAutocommitAsync(channel, token);
                        compressed?.Reset();
                    }
                }
            }
            catch (OperationCanceledException) when (_budget.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) when (_budget.IsCancellationRequested) { }
            catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
        }
        private static byte[] SessionOk(bool oracle)
        {
            using var state = new MemoryStream();
            var names = new[] { "time_zone", "ncharacter_set_connection", "character_set_results", "character_set_connection" };
            for (var i = 0; i < names.Length; i++)
            {
                var name = Encoding.ASCII.GetBytes(names[i]); var value = Encoding.ASCII.GetBytes(i == 0 ? "+00:00" : "utf8mb4");
                state.WriteByte(0); state.WriteByte((byte)(2 + name.Length + value.Length));
                state.WriteByte((byte)name.Length); state.Write(name); state.WriteByte((byte)value.Length); state.Write(value);
            }
            var status = ServerStatusFlags.Autocommit | ServerStatusFlags.ServerSessionStateChanged;
            if (oracle) status |= ServerStatusFlags.ObServerUseOracleMode;
            using var ok = new MemoryStream();
            ok.Write(new byte[] { 0, 0, 0, (byte)status, (byte)((ushort)status >> 8), 0, 0, 0, (byte)state.Length });
            ok.Write(state.ToArray()); return ok.ToArray();
        }
        public async ValueTask DisposeAsync()
        {
            _budget.Cancel(); _peer?.Dispose(); _listener.Stop();
            try { await _serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Certificate?.Dispose(); _budget.Dispose(); }
        }

        // Frame independently, using ZLibStream rather than the product's codec/stream.
        private sealed class WireStream : Stream
        {
            private readonly Stream _wire;
            private byte[] _data = Array.Empty<byte>();
            private int _position;
            private byte _sequence;
            internal WireStream(Stream wire) => _wire = wire;
            internal void Reset() { Assert.Equal(_data.Length, _position); _sequence = 0; }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_position == _data.Length)
                {
                    var header = new byte[7]; await _wire.ReadExactlyAsync(header, cancellationToken);
                    Assert.Equal(_sequence++, header[3]);
                    var body = new byte[LengthAt(header, 0)]; await _wire.ReadExactlyAsync(body, cancellationToken);
                    var original = LengthAt(header, 4);
                    if (original == 0) _data = body;
                    else
                    {
                        using var input = new MemoryStream(body); using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                        using var output = new MemoryStream(); await zlib.CopyToAsync(output, cancellationToken);
                        _data = output.ToArray(); Assert.Equal(original, _data.Length);
                    }
                    _position = 0;
                }
                var read = Math.Min(count, _data.Length - _position);
                Array.Copy(_data, _position, buffer, offset, read); _position += read; return read;
            }
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (count == 0) return;
                using var output = new MemoryStream();
                using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(buffer, offset, count);
                var body = output.ToArray(); var header = new byte[7];
                LengthTo(header, 0, body.Length); header[3] = _sequence++; LengthTo(header, 4, count);
                await _wire.WriteAsync(header, cancellationToken); await _wire.WriteAsync(body, cancellationToken);
            }
            private static int LengthAt(byte[] bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;
            private static void LengthTo(byte[] bytes, int offset, int value)
            { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); bytes[offset + 2] = (byte)(value >> 16); }
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
        }
    }
}
