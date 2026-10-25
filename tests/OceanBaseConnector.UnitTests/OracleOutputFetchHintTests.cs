using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, 1U, false)]
        [InlineData(true, 1U, false)]
        [InlineData(false, 3U, false)]
        [InlineData(true, 3U, false)]
        [InlineData(false, 1U, true)]
        [InlineData(true, 1U, true)]
        [InlineData(false, 3U, true)]
        [InlineData(true, 3U, true)]
        public Task OracleOutputFetchHintPreservesReturningNullAndInputOutput(bool asynchronous, uint flags, bool empty)
            => OracleReturningIntoUsesBinaryBindingsAndPublishesZeroRowsAsNullCore(asynchronous, flags, empty, fetchSize: 1);

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public Task OracleOutputFetchHintPreservesMultiResultCompletionAndEarlyClose(bool asynchronous, bool earlyClose)
            => OracleOutputWaitsForAllResultsAndEarlyClosePreservesOldValueCore(asynchronous, earlyClose, fetchSize: 1);

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public Task OracleOutputFetchHintPreservesValuesAfterKnownTailOrLaterError(bool asynchronous, bool laterError)
            => OracleOutputErrorsNeverPublishAndKnownErrorsAllowSessionReuseCore(asynchronous, laterError, fetchSize: 1);

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public Task OracleOutputFetchHintCancellationAndTimeoutNeverPublishOrReplay(bool asynchronous, bool timeout)
            => OpeningIdentityAndOutputCancellationNeverPublishOrReplayCore(asynchronous, versionProbe: false, timeout: timeout, fetchSize: 1);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task OracleOutputFetchHintAcceptsProcedureWithoutParameters(bool asynchronous)
            => OracleProcedureWithoutParametersUsesPrepareExecuteAndClosesItsIdCore(asynchronous, fetchSize: 1);
    }
}
