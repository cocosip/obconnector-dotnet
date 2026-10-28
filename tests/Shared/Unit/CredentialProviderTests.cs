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
    public sealed partial class CredentialProviderTests
    {
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
        private sealed class Provider : IOceanBaseCredentialProvider, IDisposable
        {
            internal Provider(OceanBaseCredential credential) => Current = credential;
            internal OceanBaseCredential Current;
            internal OceanBaseCredentialContext? Context;
            internal bool Fail = false, Disposed;
            internal int Calls;
            internal Func<CancellationToken, OceanBaseCredential>? Sync = null;
            internal Func<CancellationToken, Task<OceanBaseCredential>>? Async = null;
            public OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken token)
            { Interlocked.Increment(ref Calls); Context = context; if (Fail) throw new InvalidOperationException("secret-provider-token"); return Sync != null ? Sync(token) : Current; }
            public Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken token)
            { if (Async == null) return Task.FromResult(GetCredential(context, token)); Interlocked.Increment(ref Calls); Context = context; return Async(token); }
            public void Dispose() => Disposed = true;
            public override bool Equals(object? obj) => obj is Provider;
            public override int GetHashCode() => 1;
        }
        private static OceanBaseCredential Credential(long version, DateTimeOffset? expiresAt = null) => new("user", "password-" + version, version, expiresAt);

        internal static CredentialServer Server() => new(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
    }
}
