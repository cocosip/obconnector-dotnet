using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        public Task EveryAssetMutatesLobLocatorsAndKeepsWriteOutcomeAtOb20BoundaryOverTls(string target, bool async, string kind, bool character)
            => EveryAssetMutatesLobLocatorsAndKeepsWriteOutcomeAtOb20BoundaryCore(target, async, kind, character, encrypted: true);
    }
}
