using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.IntegrationTests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class Cp932CharacterSetTests(ITestOutputHelper output)
{
    private const string Sample = "日本纊Ⅰⅰ\ue000";

    public static IEnumerable<object[]> Clients() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && !p.HasServerLimitation("oracle-cp932"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    public static IEnumerable<object[]> National() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.Oracle)
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    public static IEnumerable<object[]> ServerLimitedClients() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects && p.HasServerLimitation("oracle-cp932"))
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, asynchronous };

    public static IEnumerable<object[]> Results() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => !p.Oracle)
        from duplicate in new[] { false, true }
        select new object[] { profile.Name, duplicate };

    [IntegrationTheory]
    [MemberData(nameof(Clients))]
    public Task Cp932ClientsPreserveIndependentStoredValues(string profile, bool asynchronous)
        => new EucCharacterSetTests(output).ClientEncodingPreservesStorageAndRejectsUnrepresentableText(profile, "cp932", Sample, prepared: asynchronous, asynchronous);

    [IntegrationTheory]
    [MemberData(nameof(National))]
    public Task Cp932NationalEncodingsPreserveIndependentStoredValues(string profile, bool asynchronous)
        => new NationalCharacterEncodingTests().ExplicitNationalEncodingsPreserveStoredScalarValues(profile, "cp932", Sample, prepareExecute: asynchronous, asynchronous);

    [IntegrationTheory]
    [MemberData(nameof(Results))]
    public Task Cp932ResultsPreserveCanonicalAndDuplicatePositions(string profile, bool duplicate)
        => new CharacterSetTests().ExplicitResultEncodingsPreserveTextAndPreparedValues(profile, "cp932",
            duplicate ? "8790ED40FA4A002020" : "93FA967BFA5C8754FA40F040002020", (duplicate ? "≒纊Ⅰ" : Sample) + "\0  ");

    [ServerLimitationTheory("oracle-cp932")]
    [MemberData(nameof(ServerLimitedClients))]
    public void OracleCp932ClientIsServerLimited(string profile, bool asynchronous)
        => Assert.Fail($"The {profile}/cp932/async={asynchronous} case is declared server-limited and must remain skipped.");
}
