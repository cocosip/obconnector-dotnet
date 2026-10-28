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
        public Task EveryAssetReadsRefCursorAfterParentCommandDisposal(string target, bool async, string kind)
            => EveryAssetReadsRefCursorAfterParentCommandDisposalCore(target, async, kind, encrypted: false);

        [Theory]
        [MemberData(nameof(OracleAssets))]
        public Task EveryAssetPreparesRefCursorAndKeepsParentAfterDisposal(string target, bool async, string kind)
            => EveryAssetReadsRefCursorAfterParentCommandDisposalCore(target, async, kind, encrypted: false, prepared: true);

        public static IEnumerable<object[]> LimitedCursorAssets()
        {
            foreach (var row in OracleAssets())
                foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult })
                    yield return new object[] { row[0], row[1], row[2], behavior };
        }

        [Theory]
        [MemberData(nameof(LimitedCursorAssets))]
        public Task EveryAssetLimitsRefCursorAcrossPagesAndValidatesFinalBoundary(string target, bool async, string kind, CommandBehavior behavior)
            => EveryAssetReadsRefCursorAfterParentCommandDisposalCore(target, async, kind, encrypted: false, behavior: behavior);
    }
}
