using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
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
        [MemberData(nameof(OracleXaAssets))]
        public Task EveryAssetExecutesOracleXaAndValidatesRecoveryBeforePublishing(string target, bool asynchronous, bool ob20, string kind)
            => EveryAssetExecutesOracleXaAndValidatesRecoveryBeforePublishingCore(target, asynchronous, ob20, kind, encrypted: false);
    }
}
