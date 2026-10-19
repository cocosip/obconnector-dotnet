using System;
using OceanBaseConnector;

namespace OceanBaseConnector.ConsumerTests.NetStandard
{
    /// <summary>
    /// Compile-time consumption sample for the netstandard2.0 asset. If a
    /// modern-only API ever leaks into the baseline asset, this file is where
    /// accidental use would compile silently; the MetadataReader assertions in
    /// the runnable consumer tests are the enforcement, this file is the smoke
    /// sample. It must only reference APIs available on netstandard2.0.
    /// </summary>
    public static class NetStandardConsumerSample
    {
        public static int? GetXaError(OceanBaseException error) => error.XaErrorCode;

        public static async System.Threading.Tasks.Task CompleteXa(OceanBaseConnection connection, OceanBaseXid xid, System.Threading.CancellationToken token)
        {
            var resource = connection.GetXaResource();
            await resource.StartAsync(xid, OceanBaseXaStartMode.New, token);
            await resource.EndAsync(xid, OceanBaseXaEndMode.Success, token);
            if (await resource.PrepareAsync(xid, token) == OceanBaseXaVote.Prepared) await resource.CommitAsync(xid, false, token);
            foreach (var recovered in await resource.RecoverAsync(token)) await resource.RollbackAsync(recovered, token);
        }

        public static string Describe()
        {
            if (OceanBaseTargetSurface.HasModernAdoNetApis)
            {
                throw new InvalidOperationException(
                    "netstandard2.0 asset must not report modern ADO.NET APIs.");
            }

            var xid = new OceanBaseXid(1, new byte[] { 1 }, Array.Empty<byte>());
            var sameXid = new OceanBaseXid(1, new byte[] { 1 }, Array.Empty<byte>());
            if (xid.FormatId != 1 || xid.GetGlobalTransactionId()[0] != 1 || xid.GetBranchQualifier().Length != 0 ||
                !((IEquatable<OceanBaseXid>)xid).Equals(sameXid) || xid.GetHashCode() != sameXid.GetHashCode())
                throw new InvalidOperationException("The baseline XID value contract is unavailable.");

            return "netstandard2.0 consumer compiles against the baseline asset.";
        }
    }
}
