using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    [Collection("Driver diagnostics")]
    public sealed partial class CredentialProviderTests
    {
        [Fact]
        public void PublicCredentialProviderSurfaceIsAvailable()
        {
            var assembly = typeof(OceanBaseConnection).Assembly;
            Assert.NotNull(assembly.GetType("OceanBaseConnector.IOceanBaseCredentialProvider"));
            Assert.NotNull(assembly.GetType("OceanBaseConnector.OceanBaseCredential"));
            Assert.NotNull(assembly.GetType("OceanBaseConnector.OceanBaseCredentialContext"));
            Assert.NotNull(typeof(OceanBaseConnection).GetProperty("CredentialProvider"));
            Assert.NotNull(typeof(OceanBaseDataSourceBuilder).GetMethod("UseCredentialProvider"));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task SnapshotReplacesStaticAuthenticationOnWire(bool asynchronous, bool pooling, bool sourceOwned)
        {
            await using var server = Server();
            var provider = new Provider(new OceanBaseCredential("rotating-user", "fresh-password", 7));
            var configuration = Configuration(server.Port, pooling);
            using var source = sourceOwned ? new OceanBaseDataSourceBuilder(configuration).UseCredentialProvider(provider).Build() : null;
            using var connection = sourceOwned ? (OceanBaseConnection)source!.CreateConnection() : new OceanBaseConnection(configuration) { CredentialProvider = provider };
            try
            {
                await Open(connection, asynchronous);
                Assert.Equal(1, provider.Calls);
                var auth = Assert.Single(server.Authentications);
                Assert.Equal("rotating-user", auth.User); Assert.Equal(NativeResponse("fresh-password"), auth.Response);
                Assert.Equal("configured-user", provider.Context!.UserId); Assert.Equal("127.0.0.1", provider.Context.Server);
                Assert.Equal(server.Port, provider.Context.Port); Assert.Equal("", provider.Context.Database);
                Assert.DoesNotContain("fresh-password", connection.ConnectionString);
                Assert.Throws<InvalidOperationException>(() => connection.CredentialProvider = null);
                await Close(connection, asynchronous);
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        public async Task EachLogicalOpenUsesOneSnapshotIncludingReuseAndWarmup(bool asynchronous, int minimum)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port, minimum: minimum)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous); var original = connection.GetOpenSession();
            await Close(connection, asynchronous); await Open(connection, asynchronous);
            Assert.Same(original, connection.GetOpenSession()); Assert.Equal(2, provider.Calls);
            Assert.Equal(Math.Max(1, minimum), server.Authentications.Count);
            Assert.All(server.Authentications, auth => Assert.Equal(NativeResponse("password-1"), auth.Response));
            await Close(connection, asynchronous);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefreshFailureDoesNotFallBackAndCanRecover(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous); var original = connection.GetOpenSession(); await Close(connection, asynchronous);
            provider.Fail = true;
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.DoesNotContain("secret-provider-token", error.ToString()); Assert.Equal(ConnectionState.Closed, connection.State);
            provider.Fail = false; await Open(connection, asynchronous);
            Assert.Same(original, connection.GetOpenSession()); Assert.Equal(1, server.Accepted);
            await Close(connection, asynchronous);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RotationRetiresIdleAndBorrowedSessionsWithoutInterruptingBorrower(bool asynchronous)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var a = (OceanBaseConnection)source.CreateConnection(); using var b = (OceanBaseConnection)source.CreateConnection();
            await Open(a, asynchronous); var borrowed = a.GetOpenSession();
            await Open(b, asynchronous); var idle = b.GetOpenSession(); await Close(b, asynchronous);
            provider.Current = Credential(2); await Open(b, asynchronous);
            Assert.Equal(SessionState.Closed, idle.State); Assert.NotSame(idle, b.GetOpenSession());
            Assert.Equal(ConnectionState.Open, a.State);
            if (asynchronous) await borrowed.ProbeAsync(new ConnectionLog(null), 2); else borrowed.Probe(new ConnectionLog(null), 2);
            await Close(a, asynchronous); Assert.Equal(SessionState.Closed, borrowed.State);
            await Close(b, asynchronous); await Open(a, asynchronous);
            Assert.Equal(3, server.Accepted); Assert.Equal(NativeResponse("password-2"), server.Authentications.Last().Response);
            await Close(a, asynchronous);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task OlderVersionOrChangedSameVersionCannotRollBackLivePool(bool asynchronous, bool sameVersion)
        {
            await using var server = Server(); var provider = new Provider(Credential(2));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            await Open(connection, asynchronous); await Close(connection, asynchronous);
            provider.Current = sameVersion ? new OceanBaseCredential("user", "changed", 2) : Credential(1);
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(1, server.Accepted); provider.Current = Credential(2);
            await Open(connection, asynchronous); Assert.Equal(1, server.Accepted); await Close(connection, asynchronous);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ProviderReferenceIdentityControlsRegistryReuseAndClear(bool asynchronous)
        {
            await using var server = Server(); var p = new Provider(Credential(1)); var q = new Provider(Credential(1));
            var configuration = Configuration(server.Port);
            using var a = new OceanBaseConnection(configuration) { CredentialProvider = p };
            using var b = new OceanBaseConnection(configuration) { CredentialProvider = q };
            try
            {
                await Open(a, asynchronous); var first = a.GetOpenSession(); await Close(a, asynchronous);
                await Open(b, asynchronous); var second = b.GetOpenSession(); Assert.NotSame(first, second); await Close(b, asynchronous);
                OceanBaseConnection.ClearPool(a); Assert.Equal(SessionState.Closed, first.State); Assert.Equal(SessionState.Open, second.State);
                b.CredentialProvider = p; await Open(b, asynchronous); Assert.NotSame(second, b.GetOpenSession()); await Close(b, asynchronous);
                a.CredentialProvider = q; await Open(a, asynchronous); Assert.Same(second, a.GetOpenSession()); await Close(a, asynchronous);
            }
            finally { a.Close(); b.Close(); a.CredentialProvider = p; b.CredentialProvider = q; OceanBaseConnection.ClearPool(a); OceanBaseConnection.ClearPool(b); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ExpiredSnapshotCannotAuthenticateOrBorrowIdleSession(bool asynchronous, bool existing)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            using var source = new OceanBaseDataSourceBuilder(Configuration(server.Port)).UseCredentialProvider(provider).Build();
            using var connection = (OceanBaseConnection)source.CreateConnection();
            if (existing) { await Open(connection, asynchronous); await Close(connection, asynchronous); }
            provider.Current = Credential(2, DateTimeOffset.UtcNow.AddSeconds(-1));
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(existing ? 1 : 0, server.Accepted); Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Theory]
        [InlineData(false, false, "timeout")]
        [InlineData(true, false, "timeout")]
        [InlineData(false, true, "close")]
        [InlineData(true, true, "close")]
        [InlineData(true, true, "cancel")]
        public async Task ProviderAcquisitionHonorsBudgetCloseAndCallerCancellation(bool asynchronous, bool pooling, string stopping)
        {
            await using var server = Server(); var provider = new Provider(Credential(1));
            var configuration = new OceanBaseConnectionStringBuilder(Configuration(server.Port, pooling)) { ConnectionTimeout = 1 }.ConnectionString;
            using var connection = new OceanBaseConnection(configuration) { CredentialProvider = provider };
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<OceanBaseCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Sync = token => { entered.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return provider.Current; };
            provider.Async = _ => { entered.TrySetResult(); return late.Task; };
            var opening = asynchronous ? connection.OpenAsync(caller.Token) : Task.Run(connection.Open);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Throws<InvalidOperationException>(() => connection.CredentialProvider = null);
                if (stopping == "close") connection.Close(); else if (stopping == "cancel") caller.Cancel();
                var error = await Record.ExceptionAsync(() => opening.WaitAsync(TimeSpan.FromSeconds(4)));
                if (stopping == "timeout") Assert.IsType<TimeoutException>(error); else Assert.IsAssignableFrom<OperationCanceledException>(error);
                late.TrySetResult(Credential(1)); await Task.Yield();
                Assert.Equal(0, server.Accepted); Assert.Equal(ConnectionState.Closed, connection.State);
                provider.Sync = null; provider.Async = null; await Open(connection, asynchronous); await Close(connection, asynchronous);
            }
            finally { late.TrySetResult(Credential(1)); connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Fact]
        public void CredentialCopiesSecretsAndRejectsInvalidBoundaries()
        {
            var passwords = new Dictionary<int, string> { [2] = "second" };
            var credential = new OceanBaseCredential("user", "primary", 1, additionalPasswords: passwords);
            passwords[2] = "mutated"; Assert.Equal("second", credential.AdditionalPasswords[2]);
            Assert.DoesNotContain("primary", credential.ToString()); Assert.DoesNotContain("second", credential.ToString());
            Assert.Throws<ArgumentException>(() => new OceanBaseCredential("", "p", 0));
            Assert.Throws<ArgumentException>(() => new OceanBaseCredential("a\0b", "p", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseCredential("u", "p", -1));
            Assert.Throws<ArgumentNullException>(() => new OceanBaseCredential("u", null!, 0));
            Assert.Throws<ArgumentException>(() => new OceanBaseCredential("u", "p", 0, additionalPasswords: new Dictionary<int, string> { [1] = "x" }));
        }

        [Fact]
        public void SourceFreezesProviderAndDoesNotOwnIt()
        {
            var p = new Provider(Credential(1)); var q = new Provider(Credential(1));
            var builder = new OceanBaseDataSourceBuilder(Configuration(1)).UseCredentialProvider(p);
            using var source = builder.Build(); builder.UseCredentialProvider(q);
            using var connection = (OceanBaseConnection)source.CreateConnection();
            Assert.Same(p, connection.CredentialProvider);
            Assert.Throws<InvalidOperationException>(() => connection.CredentialProvider = q);
            source.Dispose(); Assert.False(p.Disposed);
        }

        internal static CredentialServer Server() => new(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
        private static OceanBaseCredential Credential(long version, DateTimeOffset? expiresAt = null) => new("user", "password-" + version, version, expiresAt);
        private static string Configuration(int port, bool pooling = true, int minimum = 0) => new OceanBaseConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = port,
            UserId = "configured-user",
            Password = "static-password",
            TlsMode = OceanBaseTlsMode.Disabled,
            Pooling = pooling,
            MinPoolSize = minimum,
            MaxPoolSize = 3,
            ConnectionTimeout = 3,
            CommandTimeout = 2,
        }.ConnectionString;
        private static Task Open(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
        private static Task Close(OceanBaseConnection connection, bool asynchronous) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
        private static byte[] NativeResponse(string password)
        {
            var first = SHA1.HashData(Encoding.UTF8.GetBytes(password)); var second = SHA1.HashData(first);
            var seed = Convert.FromHexString("01020304050607081112131415161718191a1b1c");
            var last = SHA1.HashData(seed.Concat(second).ToArray());
            return first.Zip(last, (a, b) => (byte)(a ^ b)).ToArray();
        }
        private sealed class Provider : IOceanBaseCredentialProvider, IDisposable
        {
            internal Provider(OceanBaseCredential credential) => Current = credential;
            internal OceanBaseCredential Current;
            internal OceanBaseCredentialContext? Context;
            internal bool Fail, Disposed;
            internal int Calls;
            internal Func<CancellationToken, OceanBaseCredential>? Sync;
            internal Func<CancellationToken, Task<OceanBaseCredential>>? Async;
            public OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken token)
            { Interlocked.Increment(ref Calls); Context = context; if (Fail) throw new InvalidOperationException("secret-provider-token"); return Sync != null ? Sync(token) : Current; }
            public Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken token)
            { if (Async == null) return Task.FromResult(GetCredential(context, token)); Interlocked.Increment(ref Calls); Context = context; return Async(token); }
            public void Dispose() => Disposed = true;
            public override bool Equals(object? obj) => obj is Provider;
            public override int GetHashCode() => 1;
        }
    }
}
