using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        [Theory]
        [MemberData(nameof(MySqlProcedureAssets))]
        public Task EveryAssetAcceptsMySqlRoutineFetchHintAndCollectsOutputsOnClose(string target, bool asynchronous, bool ob20, bool encrypted, bool deprecated)
            => MySqlProcedureConsumerCore(target, asynchronous, ob20, encrypted, deprecated, asynchronous ? int.MaxValue : 1);
    }
}
