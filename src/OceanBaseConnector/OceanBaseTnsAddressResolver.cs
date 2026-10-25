using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;

namespace OceanBaseConnector
{
    /// <summary>Reads a fresh UTF-8 TNS address snapshot for each Open. The immutable
    /// resolver is caller-owned and safe for concurrent use. Publish file updates by
    /// atomic replacement. Groups, rotation, random and weighted selection are supported;
    /// unsupported policies and non-TCP protocols are rejected.</summary>
    public sealed class OceanBaseTnsAddressResolver : IOceanBaseAddressResolver, IDiscoveryAddressResolver
    {
        private const int MaximumBytes = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly string _path;
        private readonly string _alias;
        private readonly string? _service;

        /// <summary>Capture an explicit file path and alias. A SERVICE_NAME field requires
        /// an exact expectedServiceName; it validates file metadata, not server identity.
        /// The file never changes connection credentials, database or TLS policy.</summary>
        public OceanBaseTnsAddressResolver(string filePath, string netServiceName, string? expectedServiceName = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A TNS file path is required.", nameof(filePath));
            if (string.IsNullOrWhiteSpace(netServiceName)) throw new ArgumentException("A TNS alias is required.", nameof(netServiceName));
            if (expectedServiceName != null && string.IsNullOrWhiteSpace(expectedServiceName))
                throw new ArgumentException("An expected TNS service name cannot be empty.", nameof(expectedServiceName));
            _path = Path.GetFullPath(filePath); _alias = netServiceName.Trim(); _service = expectedServiceName;
        }

        public string Resolve(CancellationToken cancellationToken) => ResolveAsync(cancellationToken).GetAwaiter().GetResult();

        public async Task<string> ResolveAsync(CancellationToken cancellationToken) =>
            (await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false)).CanonicalAddresses;

        DiscoverySnapshot IDiscoveryAddressResolver.ResolveSnapshot(CancellationToken token) => ReadSnapshotAsync(token).GetAwaiter().GetResult();
        Task<DiscoverySnapshot> IDiscoveryAddressResolver.ResolveSnapshotAsync(CancellationToken token) => ReadSnapshotAsync(token);

        // Opening a file on a network filesystem can block before the first async read.
        private Task<DiscoverySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) => Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = await OpenFileAsync(cancellationToken).ConfigureAwait(false);
            using var contents = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await file.ReadAsync(buffer, 0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)contents.Length), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                contents.Write(buffer, 0, count);
                if (contents.Length > MaximumBytes) throw new FormatException("TNS input exceeds the 1 MiB limit.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try { text = Utf8.GetString(contents.GetBuffer(), 0, (int)contents.Length); }
            catch (DecoderFallbackException error) { throw new FormatException("TNS input must be valid UTF-8.", error); }
            return TnsAddressParser.ParseSnapshot(text, _alias, _service, cancellationToken);
        }, cancellationToken);

        private async Task<FileStream> OpenFileAsync(CancellationToken token)
        {
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                // Replacement can briefly hide or exclusively own the published file.
                catch (IOException error) when (attempt < 4 &&
                    (error is FileNotFoundException || error.HResult == unchecked((int)0x80070020) || error.HResult == unchecked((int)0x80070021)))
                {
                    await Task.Delay(25, token).ConfigureAwait(false);
                }
            }
        }
    }
}
