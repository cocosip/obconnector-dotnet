#if NET8_0_OR_GREATER
namespace OceanBaseConnector
{
    /// <summary>
    /// Modern-target (net8.0+) API surface of the provider. Members here
    /// (DbDataSource / DbBatch accessors and similar platform APIs) must
    /// never appear in the netstandard2.0 asset; the consumer tests enforce
    /// this by inspecting the built assemblies.
    /// </summary>
    public static class OceanBaseModernApis
    {
        internal const string NotYetDelivered =
            "Modern data-source and batch APIs are delivered by stage C06 (development plan).";
    }
}
#endif

namespace OceanBaseConnector
{
    /// <summary>
    /// Describes the API surface of this assembly for the current build target.
    /// A01 sample for target-conditional compilation: modern ADO.NET APIs
    /// (DbDataSource, DbBatch, async transaction entries) are compiled only
    /// for net8.0 and later; netstandard2.0 must never expose them.
    /// </summary>
    public static class OceanBaseTargetSurface
    {
        /// <summary>
        /// Gets a value indicating whether the current build target carries
        /// the modern ADO.NET API surface. Always <see langword="false"/> on
        /// the netstandard2.0 asset.
        /// </summary>
        public static bool HasModernAdoNetApis =>
#if NET8_0_OR_GREATER
            true;
#else
            false;
#endif
    }
}
