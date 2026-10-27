namespace OceanBaseConnector.ConsumerTests;

internal static class ConsumerTargetMatrix
{
    // These are the three product implementations selected by the source
    // build conditions; net8/net9 share the modern implementation.
    internal const string NetStandard20 = "netstandard2.0";
    internal const string NetStandard21 = "netstandard2.1";
    internal const string Modern = "net10.0";

    internal static readonly string[] Representative = { NetStandard20, NetStandard21, Modern };
    internal static readonly string[] ModernOnly = { Modern };
}
