using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    [Collection("Consumer diagnostics")]
    public sealed partial class Ob20TraceConsumerTests
    {

        [Theory]
        [MemberData(nameof(Cases))]
        public Task EveryAssetCorrelatesFullLinkTraceWithoutSendingApplicationData(string target, bool async, string kind)
            => CorrelatesFullLinkTrace(target, async, kind, encrypted: false);

        [Theory]
        [MemberData(nameof(ControlCases))]
        public Task FullLinkTraceCoversPrepareCursorAndTransactionControl(bool async, bool modern)
            => TracePrepareCursorAndTransactionControl(async, modern, encrypted: false);

        [Theory]
        [MemberData(nameof(FaultCases))]
        public Task TraceExtraFaultsAndPartialBodyCancellationBreakConnectionWithoutReplay(bool async, bool modern, string kind)
            => TraceFaultsAndPartialBodyCancellation(async, modern, kind, encrypted: false);
    }
}
