using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Runtime.Loader;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    [Collection("Consumer diagnostics")]
    public sealed class SourceReadConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var asset in ConsumerTargetMatrix.Representative)
                foreach (var (oracle, local) in new[] { (true, false), (false, false), (false, true) })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var outcome in new[] { "success", "error" })
                            yield return new object[] { asset, oracle, local, asynchronous, outcome };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetMeasuresSourceReadsWithoutContentTags(string asset, bool oracle, bool local, bool asynchronous, string outcome)
        {
            var context = new AssemblyLoadContext("source-read-" + asset, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(asset));
                await SourceReadFixture.Verify(() => (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!,
                    oracle, local, asynchronous, outcome, throwListener: true);
            }
            finally { context.Unload(); }
        }
    }
}
