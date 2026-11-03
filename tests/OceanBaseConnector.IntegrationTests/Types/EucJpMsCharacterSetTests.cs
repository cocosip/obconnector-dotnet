using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.IntegrationTests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class EucJpMsCharacterSetTests(ITestOutputHelper output)
{
    private const string Sample = "日本①ⅰ\ue000\ue3ac";

    public static IEnumerable<object[]> Clients() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && !p.HasServerLimitation("oracle-eucjpms"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    public static IEnumerable<object[]> National() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.Oracle)
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    public static IEnumerable<object[]> Results() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => !p.Oracle)
        from duplicate in new[] { false, true }
        select new object[] { profile.Name, duplicate };

    public static IEnumerable<object[]> ServerLimitedClients() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasServerLimitation("oracle-eucjpms"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Clients))]
    public Task EucJpMsClientsPreserveIndependentStoredValues(string profile, bool asynchronous)
        => new EucCharacterSetTests(output).ClientEncodingPreservesStorageAndRejectsUnrepresentableText(profile, "eucjpms", Sample, prepared: asynchronous, asynchronous);

    [IntegrationTheory]
    [MemberData(nameof(National))]
    public Task EucJpMsNationalEncodingsPreserveIndependentStoredValues(string profile, bool asynchronous)
        => new NationalCharacterEncodingTests().ExplicitNationalEncodingsPreserveStoredScalarValues(profile, "eucjpms", Sample, prepareExecute: asynchronous, asynchronous);

    [IntegrationTheory]
    [MemberData(nameof(Results))]
    public Task EucJpMsResultsPreserveCanonicalAndDuplicatePositions(string profile, bool duplicate)
        => new CharacterSetTests().ExplicitResultEncodingsPreserveTextAndPreparedValues(profile, "eucjpms",
            duplicate ? "ADF08FF3FD8FA2B7002020" : "C6FCCBDCADA18FF3F3F5A18FF5A1002020", (duplicate ? "≒Ⅰ～" : Sample) + "\0  ");

    [ServerLimitationTheory("oracle-eucjpms")]
    [MemberData(nameof(ServerLimitedClients))]
    public void OracleEucJpMsClientIsServerLimited(string profile, bool asynchronous)
        => Assert.Fail($"The {profile}/eucjpms/async={asynchronous} case is declared server-limited and must remain skipped.");
}
