using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> TlsValidationAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var accept in new[] { false, true }) yield return new object[] { target, asynchronous, accept };
        }

        [Theory]
        [MemberData(nameof(TlsValidationAssets))]
        public async Task EveryAssetUsesCustomTlsServerValidation(string target, bool asynchronous, bool accept)
        {
            using var certificate = TestCertificateFactory.CreateSelfSigned();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            async Task Serve()
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = new SslStream(peer.GetStream());
                var channel = new PacketChannel(peer.GetStream());
                try
                {
                    await Handshake(channel, peer.GetStream(), "unrequested", budget.Token, tls: tls, certificate: certificate);
                    Assert.True(accept);
                    Assert.Equal(3, (await ReadRequest(tls, channel, false, 0, budget.Token))[0]);
                    await channel.WritePacketAsync(Report(false, 1), budget.Token);
                    await ObserveClose(tls, budget.Token);
                }
                catch (AuthenticationException) when (!accept) { }
                catch (IOException) when (!accept) { }
                catch (ProtocolException error) when (!accept && error.Message.StartsWith("Connection closed while reading", StringComparison.Ordinal)) { }
            }
            var serving = Serve();
            var context = new AssemblyLoadContext("tls-validation-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Server Name=ob-loopback";
                var calls = 0;
                RemoteCertificateValidationCallback validation = (_, actual, chain, errors) =>
                {
                    calls++;
                    Assert.NotNull(actual); Assert.NotNull(chain);
                    Assert.Equal(certificate.Thumbprint, actual!.GetCertHashString());
                    Assert.Equal(SslPolicyErrors.RemoteCertificateChainErrors, errors);
                    return accept;
                };
                var property = connection.GetType().GetProperty("RemoteCertificateValidationCallback");
                Assert.NotNull(property);
                property!.SetValue(connection, validation);
                if (accept)
                {
                    if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "selected";
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                }
                else
                {
                    var error = await Record.ExceptionAsync(async () => { if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open(); });
                    Assert.NotNull(error);
                    Assert.IsAssignableFrom<AuthenticationException>(error.InnerException);
                    Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
                }
                Assert.Equal(1, calls);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
