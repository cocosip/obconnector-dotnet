using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class DbcsClientLiteralTests
{
    internal static readonly (string Name, string Text, string Hex)[] Samples =
    {
        ("gbk", "中文乗", "D6D0CEC4815C"),
        ("gb18030", "ḿ\ue7c7😀乗", "A8BC8135F4379439FC36815C"),
        ("gb18030_2022", "龴\ue81e\ufe10乗", "FE5982359037A6D9815C"),
        ("sjis", "日本語ソ", "93FA967B8CEA835C"), ("big5", "中•∼功", "A4A4A145A1E3A55C"),
        ("hkscs", "\uf308Ā中功", "88458856A4A4A55C"), ("hkscs31", "\U0002010cĀ中功", "88458856A4A4A55C"),
    };

    public static TheoryData<string, string, string, string, bool> Cases()
    {
        var cases = new TheoryData<string, string, string, string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(profile => !profile.Oracle))
            foreach (var (name, text, hex) in Samples)
                foreach (var asynchronous in new[] { false, true }) cases.Add(profile.Name, name, text, hex, asynchronous);
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public Task GeneratedDbcsLiteralPreservesValueAndTrailBackslash(string profile, string name, string text, string hex, bool asynchronous)
        => SingleByteClientLiteralTests.Verify(IntegrationEnvironment.GetProfile(profile), name, text, hex, asynchronous);
}
