using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

// Several suites mutate session state (XA branches, session time zone, savepoints);
// run the whole assembly sequentially against the shared databases.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

/// <summary>
/// Tags every integration fact/theory with Category=Integration so IDEs and
/// `dotnet test --filter` can exclude or select the database suites.
/// </summary>
public sealed class IntegrationCategoryDiscoverer : TraitDiscoverer
{
    private static readonly IEnumerable<KeyValuePair<string, string>> Traits =
        new[] { new KeyValuePair<string, string>("Category", "Integration") };

    public override IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttributeInfo) => Traits;
}

[TraitDiscoverer("OceanBaseConnector.IntegrationTests.Infrastructure." + nameof(IntegrationCategoryDiscoverer), "OceanBaseConnector.IntegrationTests")]
public class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!IntegrationEnvironment.Requested)
            Skip = $"Integration environment not requested; set {IntegrationEnvironment.RequireIntegrationVariable}=1 plus a profiles configuration to run.";
    }
}

[TraitDiscoverer("OceanBaseConnector.IntegrationTests.Infrastructure." + nameof(IntegrationCategoryDiscoverer), "OceanBaseConnector.IntegrationTests")]
public class IntegrationTheoryAttribute : TheoryAttribute
{
    public IntegrationTheoryAttribute()
    {
        if (!IntegrationEnvironment.Requested)
            Skip = $"Integration environment not requested; set {IntegrationEnvironment.RequireIntegrationVariable}=1 plus a profiles configuration to run.";
    }
}
