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
    public sealed class MySqlProcedureSharedConsumerTests : Ob20TestSupport
    {

        [Theory]
        [MemberData(nameof(MySqlProcedureAssets))]
        public Task EveryAssetExecutesMySqlProcedureAndPublishesOutputsOnClose(string target, bool async, bool ob20, bool encrypted, bool deprecated)
            => MySqlProcedureConsumerCore(target, async, ob20, encrypted, deprecated);

        [Theory]
        [MemberData(nameof(MySqlProcedureAssets))]
        public Task EveryAssetStreamsMySqlInOutAndPublishesOutputsOnClose(string target, bool async, bool ob20, bool encrypted, bool deprecated)
            => MySqlProcedureConsumerCore(target, async, ob20, encrypted, deprecated, inoutStream: true);

    }
}
