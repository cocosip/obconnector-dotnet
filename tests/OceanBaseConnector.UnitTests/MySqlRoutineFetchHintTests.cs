using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> MySqlProcedureFetchHintCases()
        {
            foreach (var row in MySqlProcedureCases())
                foreach (var fetchSize in new[] { 1, int.MaxValue })
                    yield return row.Concat(new object[] { fetchSize }).ToArray();
        }

        [Theory]
        [MemberData(nameof(MySqlProcedureFetchHintCases))]
        public Task MySqlProcedureFetchHintPreservesStreamingAndAtomicOutputs(bool asynchronous, bool deprecated, bool sequential, bool close, int fetchSize)
            => MySqlProcedureResultsCore(asynchronous, deprecated, sequential, close, fetchSize);

        [Theory]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, int.MaxValue)]
        [InlineData(true, int.MaxValue)]
        public Task MySqlFunctionFetchHintPreservesHiddenReturnValue(bool asynchronous, int fetchSize)
            => MySqlFunctionReturnValueCore(asynchronous, fetchSize);

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(false, 4)]
        [InlineData(true, 4)]
        public Task MySqlProcedureFetchHintPreservesOutputsAndFailureClassification(bool asynchronous, int stage)
            => MySqlProcedureFailureCore(asynchronous, stage, fetchSize: 1);
    }
}
