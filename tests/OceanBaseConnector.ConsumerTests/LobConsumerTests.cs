using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {

        [Theory]
        [MemberData(nameof(LobAssets))]
        public Task EveryAssetReadsBoundLobChunksAndValidatesOb20Completion(string target, bool async, string kind, bool character)
            => EveryAssetReadsBoundLobChunksAndValidatesOb20CompletionCore(target, async, kind, character, encrypted: false);
    }
}
