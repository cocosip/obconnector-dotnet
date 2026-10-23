using System;
using System.Linq;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

/// <summary>
/// Filtered profile data sources for [MemberData]. Each source enumerates only
/// the profiles a suite applies to, so inapplicable mode/capability
/// combinations never appear as test cases. Without the opt-in environment
/// variable these all return empty lists (the attributes add the skip reason).
/// </summary>
public static class ProfileTheoryData
{
    public static TheoryData<string> All() => For(_ => true);
    public static TheoryData<string> MySql() => For(p => !p.Oracle);
    public static TheoryData<string> Oracle() => For(p => p.Oracle);
    public static TheoryData<string> WithCreateObjects() => For(p => p.CreateObjects);
    public static TheoryData<string> MySqlWithCreateObjects() => For(p => !p.Oracle && p.CreateObjects);
    public static TheoryData<string> OracleWithCreateObjects() => For(p => p.Oracle && p.CreateObjects);
    public static TheoryData<string> WithSlowQuery() => For(p => p.SlowQuery is not null);
    public static TheoryData<string> WithAlternateDatabase() => For(p => !p.Oracle && p.AlternateDatabase is not null);
    public static TheoryData<string> WithPrepareExecute() => For(p => p.Oracle && PrepareExecute(p));
    public static TheoryData<string> WithLobLocators() => For(p => p.Oracle && PrepareExecute(p) && LobLocators(p));
    public static TheoryData<string> WithUnsupportedAuth() => For(p => p.UnsupportedAuthConnectionString is not null);
    public static TheoryData<string> WithAutocommitDisabled() => For(p => p.AutocommitDisabledConnectionString is not null);
    public static TheoryData<string> WithTag(string tag) => For(p => p.HasTag(tag));
    public static TheoryData<string> XaCapable() => For(p => p.CreateObjects && (!p.Oracle || PrepareExecute(p)));
    public static TheoryData<string> OraclePrepareExecuteObjects() => For(p => p.Oracle && p.CreateObjects && PrepareExecute(p));
    public static TheoryData<string> WithOb20Trace() => For(p => new OceanBaseConnectionStringBuilder(p.ConnectionString).EnableFullLinkTrace);

    private static bool PrepareExecute(IntegrationProfile profile) =>
        new OceanBaseConnectionStringBuilder(profile.ConnectionString).UseOraclePrepareExecute;

    private static bool LobLocators(IntegrationProfile profile) =>
        new OceanBaseConnectionStringBuilder(profile.ConnectionString).UseLobLocators;

    private static TheoryData<string> For(Func<IntegrationProfile, bool> predicate)
    {
        var data = new TheoryData<string>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(predicate))
            data.Add(profile.Name);
        return data;
    }
}
