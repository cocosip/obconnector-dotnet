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
        [MemberData(nameof(OracleAssets))]
        public Task EveryAssetReadsRefCursorAfterParentCommandDisposalOverTls(string target, bool async, string kind)
            => EveryAssetReadsRefCursorAfterParentCommandDisposalCore(target, async, kind, encrypted: true);

        [Theory]
        [MemberData(nameof(OracleAssets))]
        public Task EveryAssetPreparesRefCursorAndKeepsParentAfterDisposalOverTls(string target, bool async, string kind)
            => EveryAssetReadsRefCursorAfterParentCommandDisposalCore(target, async, kind, encrypted: true, prepared: true);
    }
}
