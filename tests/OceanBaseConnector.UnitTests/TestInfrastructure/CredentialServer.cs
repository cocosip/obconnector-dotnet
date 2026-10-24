using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;
using OceanBaseConnector.UnitTests.Transport;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    // Synthetic native-auth wire peer, not real OceanBase/plugin interoperability.
    internal sealed class CredentialServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentBag<Task> _clients = new ConcurrentBag<Task>();
        private readonly Task _accepting;
        private readonly byte[] _greeting;
        private readonly bool _oracle;
        private readonly bool _pam;
        private readonly byte[]? _sessionStateOk;
        private readonly X509Certificate2? _certificate;
        private int _accepted;
        internal CredentialServer(byte[] greeting, bool oracle = true, bool pam = false, byte[]? sessionStateOk = null)
        {
            _greeting = greeting; _oracle = oracle; _pam = pam; _sessionStateOk = sessionStateOk;
            if (pam) _certificate = TestCertificateFactory.CreateSelfSigned();
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _accepting = Accept();
        }
        internal int Port { get; }
        internal int Accepted => Volatile.Read(ref _accepted);
        internal ConcurrentQueue<(string User, byte[] Response)> Authentications { get; } = new();
        internal ConcurrentQueue<byte[]> PamResponses { get; } = new();
        internal Func<Task>? BeforeGreeting { get; set; }
        internal Func<Task>? BeforeIdentity { get; set; }
        internal Func<Task>? BeforeResetReply { get; set; }
        internal Func<Task>? BeforeProbeReply { get; set; }
        internal Func<Task>? BeforeAuthSwitch { get; set; }
        private async Task Accept()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted); _clients.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                var channel = new PacketChannel(client.GetStream());
                using var tls = _certificate == null ? null : new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
                var ok = _sessionStateOk ?? new byte[] { 0, 0, 0, (byte)(_oracle ? 6 : 2), 0, 0, 0 };
                try
                {
                    if (BeforeGreeting != null) await BeforeGreeting().WaitAsync(_stop.Token);
                    await channel.WritePacketAsync(_greeting, _stop.Token);
                    if (tls != null)
                    {
                        Assert.Equal(32, (await channel.ReadPacketAsync(_stop.Token)).Length);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                        channel.ReplaceUnderlyingStream(tls);
                    }
                    var auth = await channel.ReadPacketAsync(_stop.Token);
                    var end = Array.IndexOf(auth, (byte)0, 32); Assert.True(end >= 32);
                    var length = auth[end + 1];
                    Authentications.Enqueue((Encoding.UTF8.GetString(auth, 32, end - 32), auth.AsSpan(end + 2, length).ToArray()));
                    if (_pam)
                    {
                        if (BeforeAuthSwitch != null) await BeforeAuthSwitch().WaitAsync(_stop.Token);
                        await channel.WritePacketAsync(new byte[] { 254, 100, 105, 97, 108, 111, 103, 0, 4, 0 }, _stop.Token);
                        PamResponses.Enqueue(await channel.ReadPacketAsync(_stop.Token));
                        await channel.WritePacketAsync(new byte[] { 4, 0 }, _stop.Token);
                        PamResponses.Enqueue(await channel.ReadPacketAsync(_stop.Token));
                    }
                    await channel.WritePacketAsync(ok, _stop.Token);
                    if (BeforeIdentity != null) await BeforeIdentity().WaitAsync(_stop.Token);
                    await ServerIdentity.ReplyAsync(channel, oracle: _oracle, token: _stop.Token);
                    while (true)
                    {
                        var request = await channel.ReadPacketAsync(_stop.Token);
                        Assert.Contains(request[0], new byte[] { 3, 0x1f, 0x0e, 2 });
                        if (request[0] == 0x1f && BeforeResetReply != null) await BeforeResetReply().WaitAsync(_stop.Token);
                        if (request[0] == 0x0e && BeforeProbeReply != null) await BeforeProbeReply().WaitAsync(_stop.Token);
                        await channel.WritePacketAsync(ok, _stop.Token); channel.BeginCommand();
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (ProtocolException error) when (error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
                catch (IOException) { }
                catch (AuthenticationException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); await _accepting; _listener.Stop();
            await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(5)); _certificate?.Dispose(); _stop.Dispose();
        }
    }
}
