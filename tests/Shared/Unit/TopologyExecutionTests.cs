using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private sealed class AddressResolver : IOceanBaseAddressResolver, IDisposable
        {
            internal Func<CancellationToken, string> Sync = _ => throw new InvalidOperationException("Wrong resolver method.");
            internal Func<CancellationToken, Task<string>> Async = _ => throw new InvalidOperationException("Wrong resolver method.");
            internal int Calls;
            internal int DisposeCount;
            public void Dispose() => DisposeCount++;
            public string Resolve(CancellationToken token) { Interlocked.Increment(ref Calls); return Sync(token); }
            public Task<string> ResolveAsync(CancellationToken token) { Interlocked.Increment(ref Calls); return Async(token); }
            internal static AddressResolver Returning(string addresses) => new AddressResolver
            { Sync = _ => addresses, Async = _ => Task.FromResult(addresses) };
        }
    }
}
