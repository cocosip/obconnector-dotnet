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
        [MemberData(nameof(ComplexDictionaryAssets))]
        public Task EveryAssetExpandsNamedObjectScriptInputsWithoutBinaryPrepare(string target, bool async, bool ob20)
            => EveryAssetLoadsExactComplexDictionaryAndCreatesImmutableValuesCore(target, async, ob20, encrypted: false, objectScript: true);

        [Theory]
        [MemberData(nameof(ComplexDictionaryAssets))]
        public Task EveryAssetExpandsNestedNamedObjectScriptInputsWithoutBinaryPrepare(string target, bool async, bool ob20)
            => EveryAssetLoadsExactComplexDictionaryAndCreatesImmutableValuesCore(target, async, ob20, encrypted: false, objectScript: true, nestedObjectScript: true);

        [Theory]
        [MemberData(nameof(ComplexDictionaryAssets))]
        public Task EveryAssetLoadsExactComplexDictionaryAndCreatesImmutableValues(string target, bool async, bool ob20)
            => EveryAssetLoadsExactComplexDictionaryAndCreatesImmutableValuesCore(target, async, ob20, encrypted: false);
    }
}
