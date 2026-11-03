using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.IntegrationTests.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class LegacySingleByteCharacterSetTests(ITestOutputHelper output)
{
    internal static readonly (string Name, string Text, string Hex)[] Samples =
    {
        ("cp850", "Çøı", "809BD5"), ("hp8", "À₤Ÿ", "A1AFEE"), ("macroman", "Ä€ﬁ", "80DBDE"),
    };

    public static IEnumerable<object[]> Clients() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => p.CreateObjects)
        from sample in Samples
        from asynchronous in new[] { false, true }
        select new object[] { profile.Name, sample.Name, sample.Text, asynchronous };

    public static IEnumerable<object[]> National() =>
        Clients().Where(row => IntegrationEnvironment.GetProfile((string)row[0]).Oracle);

    public static IEnumerable<object[]> Results() =>
        from profile in IntegrationEnvironment.Profiles.Where(p => !p.Oracle)
        from sample in Samples.Concat(new[] { (Name: "swe7", Text: "ÉÄÖÅÜéäöåü", Hex: "405B5C5D5E607B7C7D7E") })
        select new object[] { profile.Name, sample.Name, sample.Hex, sample.Text };

    [IntegrationTheory]
    [MemberData(nameof(Clients))]
    public Task LegacySingleByteClientsPreserveIndependentStoredValues(string profile, string charset, string text, bool asynchronous)
        => new EucCharacterSetTests(output).ClientEncodingPreservesStorageAndRejectsUnrepresentableText(profile, charset, text, prepared: asynchronous, asynchronous);

    [IntegrationTheory]
    [MemberData(nameof(National))]
    public Task LegacySingleByteNationalEncodingsPreserveIndependentStoredValues(string profile, string charset, string text, bool asynchronous)
        => new NationalCharacterEncodingTests().ExplicitNationalEncodingsPreserveStoredScalarValues(profile, charset, text, prepareExecute: asynchronous, asynchronous);

    [IntegrationTheory]
    [MemberData(nameof(Results))]
    public Task LegacySingleByteResultsPreserveFixedBytesAcrossReaderModes(string profile, string charset, string hex, string text)
        => new CharacterSetTests().ExplicitResultEncodingsPreserveTextAndPreparedValues(profile, charset, hex + "002020", text + "\0  ");
}
