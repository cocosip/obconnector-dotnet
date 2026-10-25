using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    // Local transport evidence only; this does not emulate server mTLS policy or compatibility.
    internal sealed class MutualTlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        private readonly ConcurrentBag<Task> _clients = new ConcurrentBag<Task>();
        private readonly ConcurrentBag<TcpClient> _peers = new ConcurrentBag<TcpClient>();
        private readonly Task _accepting;
        private readonly byte[] _greeting;
        private readonly Func<PacketChannel, Task<byte[]>> _readCommand;
        private readonly bool _oracle;
        private int _accepted;
        private int _resets;
        public MutualTlsServer(byte[] greeting, Func<PacketChannel, Task<byte[]>> readCommand, bool oracle = true)
        { _greeting = greeting; _readCommand = readCommand; _oracle = oracle; Certificate = TestCertificateFactory.CreateSelfSigned(); _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _accepting = Accept(); }
        public X509Certificate2 Certificate { get; }
        public int Port { get; }
        public int Accepted => Volatile.Read(ref _accepted);
        public int Resets => Volatile.Read(ref _resets);
        public Func<byte, Task>? BeforeCommandReply { get; set; }
        public ConcurrentQueue<string> AuthenticatedCertificates { get; } = new ConcurrentQueue<string>();
        public Func<X509Certificate, bool>? AcceptClientCertificate { get; set; }
        private async Task Accept()
        {
            try
            {
                while (true)
                {
                    var peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _peers.Add(peer);
                    Interlocked.Increment(ref _accepted);
                    _clients.Add(Serve(peer));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        private async Task Serve(TcpClient peer)
        {
            using (peer)
            using (var tls = new SslStream(peer.GetStream(), false,
                (_, certificate, _, _) => certificate != null && (AcceptClientCertificate?.Invoke(certificate) ?? true)))
            {
                peer.NoDelay = true;
                var channel = new PacketChannel(peer.GetStream());
                try
                {
                    await channel.WritePacketAsync(_greeting, _stop.Token);
                    Assert.Equal(32, (await channel.ReadPacketAsync(_stop.Token)).Length);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate, ClientCertificateRequired = true }, _stop.Token);
                    Assert.NotNull(tls.RemoteCertificate);
                    channel.ReplaceUnderlyingStream(tls);
                    await channel.ReadPacketAsync(_stop.Token);
                    AuthenticatedCertificates.Enqueue(tls.RemoteCertificate!.GetCertHashString());
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, _oracle ? (byte)6 : (byte)2, 0, 0, 0 }, _stop.Token);
                    await ServerIdentity.ReplyAsync(channel, oracle: _oracle, token: _stop.Token);
                    while (true)
                    {
                        var request = await _readCommand(channel);
                        Assert.Contains(request[0], new byte[] { 3, 0x1f, 0x0e });
                        if (request[0] == 0x1f) Interlocked.Increment(ref _resets);
                        if (BeforeCommandReply != null) await BeforeCommandReply(request[0]);
                        await channel.WritePacketAsync(new byte[] { 0, request[0] == 3 ? (byte)1 : (byte)0, 0, 6, 0, 0, 0 }, _stop.Token);
                        if (_oracle && request[0] == 0x1f) await ServerIdentity.ReplyOracleAutocommitAsync(channel, _stop.Token);
                        channel.BeginCommand();
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (AuthenticationException) { }
                catch (IOException) { }
                catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); await _accepting; _listener.Stop();
            foreach (var peer in _peers) peer.Dispose();
            await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(5));
            Certificate.Dispose(); _stop.Dispose();
        }
    }
}
