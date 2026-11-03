using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.IntegrationTests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class UjisCharacterSetTests(ITestOutputHelper output)
{
    private const string Sample = "日本ｶ丂\ue000\ue3ac";

    public static IEnumerable<object[]> Clients() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && !p.HasServerLimitation("oracle-ujis"))
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
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasServerLimitation("oracle-ujis"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    [IntegrationTheory]
    [MemberData(nameof(Clients))]
    public Task UjisClientsPreserveIndependentStoredValues(string profile, bool asynchronous)
        => new EucCharacterSetTests(output).ClientEncodingPreservesStorageAndRejectsUnrepresentableText(profile, "ujis", Sample, prepared: asynchronous, asynchronous);

    [IntegrationTheory]
    [MemberData(nameof(National))]
    public Task UjisNationalEncodingsPreserveIndependentStoredValues(string profile, bool asynchronous)
        => new NationalCharacterEncodingTests().ExplicitNationalEncodingsPreserveStoredScalarValues(profile, "ujis", Sample, prepareExecute: asynchronous, asynchronous);

    [IntegrationTheory]
    [MemberData(nameof(Results))]
    public Task UjisResultsPreserveCanonicalAndAsciiAliasPositions(string profile, bool duplicate)
        => new CharacterSetTests().ExplicitResultEncodingsPreserveTextAndPreparedValues(profile, "ujis",
            duplicate ? "A1C08FA2B7002020" : "C6FCCBDC8EB68FB0A1F5A18FF5A1002020", (duplicate ? "\\~" : Sample) + "\0  ");

    [ServerLimitationTheory("oracle-ujis")]
    [MemberData(nameof(ServerLimitedClients))]
    public void OracleUjisClientIsServerLimited(string profile, bool asynchronous)
        => Assert.Fail($"The {profile}/ujis/async={asynchronous} case is declared server-limited and must remain skipped.");
}
