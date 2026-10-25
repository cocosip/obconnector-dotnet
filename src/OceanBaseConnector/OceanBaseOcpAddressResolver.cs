using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;

namespace OceanBaseConnector
{
    /// <summary>Fetches a fresh OCP TCP address snapshot before each Open. The caller owns
    /// the concurrency-safe HttpClient, including its authentication, TLS and redirect policy.
    /// Rich load policies are rejected rather than silently flattened.</summary>
    public sealed class OceanBaseOcpAddressResolver : IOceanBaseAddressResolver
    {
        private const int MaximumBytes = 1024 * 1024;
        private readonly HttpClient _client;
        private readonly Uri _uri;
        private readonly string _name;
        private readonly string? _service;

        public OceanBaseOcpAddressResolver(HttpClient httpClient, Uri server, string netServiceName, string? expectedServiceName = null)
        {
            _client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (!server.IsAbsoluteUri || (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps) ||
                server.UserInfo.Length != 0 || server.AbsolutePath != "/" || server.Query.Length != 0 || server.Fragment.Length != 0)
                throw new ArgumentException("OCP server must be an HTTP(S) origin without user information, path, query or fragment.", nameof(server));
            if (string.IsNullOrWhiteSpace(netServiceName)) throw new ArgumentException("An OCP net service name is required.", nameof(netServiceName));
            if (expectedServiceName != null && string.IsNullOrWhiteSpace(expectedServiceName))
                throw new ArgumentException("An expected service name cannot be empty.", nameof(expectedServiceName));
            _name = netServiceName.Trim(); _service = expectedServiceName;
            _uri = new Uri(server, "/api/v2/obproxy/loadBalanceInfo?appName=" + Uri.EscapeDataString(_name));
        }

        public string Resolve(CancellationToken cancellationToken) => ResolveAsync(cancellationToken).GetAwaiter().GetResult();

        public async Task<string> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("OCP discovery requires HTTP 200.");
            if (response.RequestMessage?.RequestUri != null && response.RequestMessage.RequestUri != _uri)
                throw new HttpRequestException("OCP discovery does not accept redirected responses.");
            var content = response.Content ?? throw new FormatException("OCP discovery requires a JSON body.");
            var type = content.Headers.ContentType;
            if (type?.MediaType == null || !(string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                    type.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(type.CharSet) && !string.Equals(type.CharSet!.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)) ||
                content.Headers.ContentEncoding.Count != 0)
                throw new FormatException("OCP discovery requires an unencoded UTF-8 JSON body.");
            if (content.Headers.ContentLength > MaximumBytes) throw new FormatException("OCP response exceeds the 1 MiB limit.");
            using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)body.Length), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                body.Write(buffer, 0, count);
                if (body.Length > MaximumBytes) throw new FormatException("OCP response exceeds the 1 MiB limit.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return OcpAddressParser.Parse(body.ToArray(), _name, _service, cancellationToken);
        }
    }
}
