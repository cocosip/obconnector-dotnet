using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TlsServerValidationTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CustomPolicyReceivesCertificateChainAndPlatformNameErrors(bool asynchronous)
        {
            await using var server = new TlsServer();
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            var calls = 0;
            SetPolicy(connection, (_, certificate, chain, errors) =>
            {
                Interlocked.Increment(ref calls);
                Assert.NotNull(certificate);
                Assert.NotNull(chain);
                Assert.Equal(server.Certificate.Thumbprint, certificate!.GetCertHashString());
                Assert.NotEqual(SslPolicyErrors.None, errors & SslPolicyErrors.RemoteCertificateChainErrors);
                Assert.Equal(SslPolicyErrors.None, errors & SslPolicyErrors.RemoteCertificateNameMismatch);
                return true;
            });
            await Open(connection, asynchronous);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(1, calls);
            Assert.Equal(1, server.Authenticated);
            Assert.Throws<InvalidOperationException>(() => SetPolicy(connection, null));
            await Close(connection, asynchronous);
            SetPolicy(connection, null);
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.IsAssignableFrom<AuthenticationException>(error.InnerException);
            Assert.Equal(1, server.Authenticated);
        }

        [Theory]
        [InlineData(false, "reject")]
        [InlineData(true, "reject")]
        [InlineData(false, "throw")]
        [InlineData(true, "throw")]
        [InlineData(false, "hostname")]
        [InlineData(true, "hostname")]
        public async Task PolicyRejectionOrExceptionNeverSendsAuthenticationOrRetries(bool asynchronous, string kind)
        {
            await using var server = new TlsServer();
            using var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start();
            var builder = new OceanBaseConnectionStringBuilder(Configuration(server.Port));
            builder.Server = $"127.0.0.1:{server.Port},127.0.0.1:{((IPEndPoint)unused.LocalEndpoint).Port}";
            if (kind == "hostname") builder.TlsServerName = "wrong-host.invalid";
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            var calls = 0;
            SetPolicy(connection, (_, _, _, errors) =>
            {
                calls++;
                if (kind == "throw") throw new InvalidOperationException("policy rejected");
                if (kind == "hostname") Assert.NotEqual(SslPolicyErrors.None, errors & SslPolicyErrors.RemoteCertificateNameMismatch);
                return false;
            });
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(1, calls);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, server.Authenticated);
            Assert.False(unused.Pending());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EqualDelegatesHaveDifferentPoolsAndReassignmentUsesMatchingPolicy(bool asynchronous)
        {
            await using var server = new TlsServer();
            var policy = new Policy();
            var first = new RemoteCertificateValidationCallback(policy.Validate);
            var equal = new RemoteCertificateValidationCallback(policy.Validate);
            Assert.Equal(first, equal); Assert.NotSame(first, equal);
            var configuration = Configuration(server.Port, pooling: true, minimum: 2);
            using var a = new OceanBaseConnection(configuration);
            using var b = new OceanBaseConnection(configuration);
            SetPolicy(a, first); SetPolicy(b, equal);
            var firstPool = PhysicalSessionPoolRegistry.Get(configuration, null, first);
            var secondPool = PhysicalSessionPoolRegistry.Get(configuration, null, equal);
            try
            {
                await Open(a, asynchronous);
                var original = a.GetOpenSession();
                await Close(a, asynchronous);
                await Open(b, asynchronous);
                var other = b.GetOpenSession();
                Assert.NotSame(original, other);
                await Close(b, asynchronous);
                Assert.Equal(4, policy.Calls);
                SetPolicy(a, equal);
                await Open(a, asynchronous);
                Assert.Same(other, a.GetOpenSession());
                await Close(a, asynchronous);
                SetPolicy(b, null);
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(b, asynchronous));
                Assert.Equal(4, server.Authenticated);
                Assert.Equal(4, policy.Calls);
                SetPolicy(a, first);
                await Open(a, asynchronous);
                Assert.Same(original, a.GetOpenSession());
                await Close(a, asynchronous);
            }
            finally
            {
                a.Close(); b.Close();
                ClearPool(b, configuration);
                firstPool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, firstPool);
                secondPool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, secondPool);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SourcePoliciesAreFixedAndBuildersSnapshotAtBuild(bool asynchronous)
        {
            await using var server = new TlsServer();
            var builder = new OceanBaseDataSourceBuilder(Configuration(server.Port, pooling: true));
            var policy = new Policy();
            var callback = new RemoteCertificateValidationCallback(policy.Validate);
            var method = typeof(OceanBaseDataSourceBuilder).GetMethod("UseRemoteCertificateValidationCallback");
            Assert.NotNull(method);
            Assert.Same(builder, method!.Invoke(builder, new object?[] { callback }));
            using var source = builder.Build();
            method.Invoke(builder, new object?[] { new RemoteCertificateValidationCallback((_, _, _, _) => false) });
            using var rejectedSource = builder.Build();
            using var a = (OceanBaseConnection)source.CreateConnection();
            using var b = (OceanBaseConnection)source.CreateConnection();
            using var rejected = (OceanBaseConnection)rejectedSource.CreateConnection();
            Assert.Same(callback, GetPolicy(a));
            Assert.Throws<InvalidOperationException>(() => SetPolicy(a, callback));
            await Open(a, asynchronous);
            var session = a.GetOpenSession();
            await Close(a, asynchronous);
            await Open(b, asynchronous);
            Assert.Same(session, b.GetOpenSession());
            await Close(b, asynchronous);
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(rejected, asynchronous));
            Assert.Equal(1, policy.Calls);
            Assert.Equal(1, server.Authenticated);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task FailedWarmupRemovesOriginalPolicyPoolAndClosesEarlierSessions(bool asynchronous, bool throws)
        {
            await using var server = new TlsServer();
            var configuration = Configuration(server.Port, pooling: true, minimum: 2);
            using var connection = new OceanBaseConnection(configuration);
            var calls = 0;
            RemoteCertificateValidationCallback callback = (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1) return true;
                if (throws) throw new InvalidOperationException("rejected second warmup");
                return false;
            };
            SetPolicy(connection, callback);
            var original = PhysicalSessionPoolRegistry.Get(configuration, null, callback);
            PhysicalSessionPool? replacement = null;
            try
            {
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.Equal(ConnectionState.Closed, connection.State);
                Assert.Equal(2, calls);
                Assert.Equal(1, server.Authenticated);
                Assert.True(original.IsEmpty);
                replacement = PhysicalSessionPoolRegistry.Get(configuration, null, callback);
                Assert.NotSame(original, replacement);
            }
            finally
            {
                connection.Close(); original.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, original);
                if (replacement != null) { replacement.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, replacement); }
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task OpeningPolicyIsFrozenAndCanceledValidationCannotPublishSession(bool asynchronous, bool callerCancel)
        {
            await using var server = new TlsServer();
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var caller = new CancellationTokenSource();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPolicy(connection, (_, _, _, _) => { entered.TrySetResult(); return release.Wait(TimeSpan.FromSeconds(5)); });
            var opening = asynchronous ? connection.OpenAsync(caller.Token) : Task.Run(connection.Open);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ConnectionState.Connecting, connection.State);
                Assert.Throws<InvalidOperationException>(() => SetPolicy(connection, null));
                if (callerCancel) caller.Cancel(); else await Close(connection, asynchronous);
            }
            finally { release.Set(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Equal(0, server.Authenticated);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClosedCallbackPolicyChangeKeepsDetachedSessionInOriginalPool(bool asynchronous)
        {
            await using var server = new TlsServer();
            var configuration = Configuration(server.Port, pooling: true);
            var policy = new Policy();
            var original = new RemoteCertificateValidationCallback(policy.Validate);
            var replacement = new RemoteCertificateValidationCallback(policy.Validate);
            using var connection = new OceanBaseConnection(configuration);
            using var next = new OceanBaseConnection(configuration);
            SetPolicy(connection, original); SetPolicy(next, original);
            var firstPool = PhysicalSessionPoolRegistry.Get(configuration, null, original);
            var secondPool = PhysicalSessionPoolRegistry.Get(configuration, null, replacement);
            try
            {
                await Open(connection, asynchronous);
                var oldSession = connection.GetOpenSession();
                StateChangeEventHandler? reenter = null;
                reenter = (_, args) =>
                {
                    if (args.CurrentState != ConnectionState.Closed) return;
                    connection.StateChange -= reenter;
                    SetPolicy(connection, replacement);
                    connection.Open();
                };
                connection.StateChange += reenter;
                await Close(connection, asynchronous);
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.NotSame(oldSession, connection.GetOpenSession());
                await Open(next, asynchronous);
                Assert.Same(oldSession, next.GetOpenSession());
                await Close(next, asynchronous); await Close(connection, asynchronous);
                Assert.Equal(2, policy.Calls);
            }
            finally
            {
                connection.Close(); next.Close(); firstPool.Dispose(); secondPool.Dispose();
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, firstPool);
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, secondPool);
            }
        }
    }
}
