using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class OcpAddressResolverTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PublicResolverRequestsBoundServiceAndReadsFreshSnapshots(bool asynchronous)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881));
            var resolver = Create(http);
            Assert.Equal("127.0.0.1:2881", await Resolve(resolver, asynchronous));
            http.Body = OcpHttpFixture.Entry(2882, "::1");
            Assert.Equal("[::1]:2882", await Resolve(resolver, asynchronous));
            Assert.Equal(2, http.Requests.Count);
            foreach (var request in http.Requests)
                Assert.Equal("https://ocp.example/api/v2/obproxy/loadBalanceInfo?appName=APP", request.AbsoluteUri);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PreCanceledDiscoveryNeverSendsAnHttpRequest(bool asynchronous)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881)); var resolver = Create(http);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolve(resolver, asynchronous, stop.Token));
            Assert.Empty(http.Requests);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task EveryOpenDiscoversBeforeIdleReuseAndRetiresRemovedEndpoints(bool asynchronous, bool oracle, bool pooled)
        {
            var greeting = ProtocolFixture.Load("initial-handshake-oceanbase").Payload;
            await using var first = new CredentialServer(greeting, oracle: oracle, sessionStateOk: oracle ? TextCommandTests.TimeZoneOk("+00:00") : null);
            await using var second = new CredentialServer(greeting, oracle: oracle, sessionStateOk: oracle ? TextCommandTests.TimeZoneOk("+00:00") : null);
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(first.Port)); var resolver = Create(http);
            using var connection = new OceanBaseConnection($"Server=localhost;Port=1;User ID=u;TLS Mode=Disabled;Pooling={pooled};Max Pool Size=1;Connection Timeout=5") { AddressResolver = resolver };
            try
            {
                await Open(connection, asynchronous); var original = connection.GetOpenSession(); connection.Close();
                http.Body = "{}";
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Equal(1, first.Accepted);
                http.Body = OcpHttpFixture.Entry(first.Port); await Open(connection, asynchronous);
                if (pooled) Assert.Same(original, connection.GetOpenSession());
                connection.Close(); http.Body = OcpHttpFixture.Entry(second.Port); await Open(connection, asynchronous);
                Assert.Equal("127.0.0.1:" + second.Port, connection.GetOpenSession().Endpoint);
                Assert.Equal(SessionState.Closed, original.State);
                using var command = connection.CreateCommand(); command.CommandText = "select ocp resolved";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData("")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("null")]
        [InlineData("{\"data\":null}")]
        [InlineData("{\"data\":{},\"data\":{}}")]
        [InlineData("{\"meta\":{\"x\":1,\"x\":2},\"data\":{}}")]
        [InlineData("{\"data\":{},}")]
        [InlineData("/*comment*/{}")]
        [InlineData("{} true")]
        [InlineData("{'data':{}}")]
        [InlineData("{\"data\":{\"NET_SERVICE_NAME\":\"OTHER\",\"DESCRIPTION\":{}}}")]
        [InlineData("{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":null}}")]
        [InlineData("{\"data\":{\"NET_SERVICE_NAME\":42,\"DESCRIPTION\":{}}}")]
        public async Task InvalidJsonAndEnvelopeNeverYieldAddresses(string body)
        {
            using var http = new OcpHttpFixture(body);
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("\"successful\":false,")]
        [InlineData("\"success\":false,")]
        [InlineData("\"successful\":\"true\",")]
        [InlineData("\"status\":500,")]
        [InlineData("\"status\":\"200\",")]
        public async Task FailureEnvelopeCannotPublishOtherwiseValidData(string prefix)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881).Replace("\"successful\":true,\"status\":200,", prefix));
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("01")]
        [InlineData("-01")]
        [InlineData("+1")]
        [InlineData("1.")]
        [InlineData("1e")]
        [InlineData("1oops")]
        [InlineData("NaN")]
        [InlineData("- 1")]
        [InlineData("1 .2")]
        [InlineData("1 e2")]
        public async Task InvalidNumberTokensAreRejectedEvenInIgnoredEnvelopeMetadata(string number)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881).Insert(1, "\"extra\":" + number + ","));
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("\"data\":{", "\"data\":{\"__type\":\"x\",")]
        [InlineData("\"DESCRIPTION\":{", "\"DESCRIPTION\":{\"__type\":\"x\",\"__type\":\"y\",")]
        [InlineData("\"ADDRESS\":[{", "\"ADDRESS\":[{\"__type\":\"x\",")]
        public async Task SerializerTypeMetadataCannotHideAsXmlAttributes(string find, string replace)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881).Replace(find, replace));
            await Assert.ThrowsAsync<NotSupportedException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ValidWhitespaceUnicodeEscapesAndMetadataNumbersRemainSupported()
        {
            var entry = OcpHttpFixture.Entry(2881).Replace("\"APP\"", "\"\\u0041PP\"").Replace("\"status\":200,", "\"status\" : 200 \r\n ,\"extra\" : [-1.25e+3, 0, true, false, null, \"\\\"\\\\\\/\\b\\f\\n\\r\\t\\u4e2d\"],");
            using var http = new OcpHttpFixture("\uFEFF \r\n" + entry + "\t");
            Assert.Equal("127.0.0.1:2881", await Create(http).ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task TruncatedDescriptorsCannotBecomeCompleteXmlSnapshots(int missing)
        {
            var entry = OcpHttpFixture.Entry(2881);
            using var http = new OcpHttpFixture(entry.Substring(0, entry.Length - missing));
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("\"PORT\":2881}", "\"PORT\":2881,}")]
        [InlineData("\"PORT\":2881}", "\"PORT\":02881}")]
        [InlineData("\"PORT\":2881}]", "\"PORT\":2881},]")]
        [InlineData("\"status\":200,", "\"status\":200,,")]
        [InlineData("\"status\":200,", "\"status\":200,/*x*/")]
        [InlineData("\"status\":200,", "\"status\":200,\u00a0")]
        public async Task JsonPunctuationCannotBeRepairedByThePlatformReader(string find, string replace)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881).Replace(find, replace));
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("\"OBLB_STRATEGY\":\"RANDOM\",")]
        [InlineData("\"OBLB_GROUP_STRATEGY\":\"ROTATION\",")]
        [InlineData("\"OBLB_BLACKLIST\":{},")]
        [InlineData("\"OBLB_RETRY_ALL_DOWNS\":2,")]
        [InlineData("\"OBLB\":\"OFF\",")]
        [InlineData("\"UNKNOWN\":true,")]
        [InlineData("\"__type\":\"unsupported\",")]
        public async Task SupportedPoliciesAndUnknownDescriptorMembersAreValidated(string prefix)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881).Replace("\"DESCRIPTION\":{", "\"DESCRIPTION\":{" + prefix));
            if (prefix.StartsWith("\"OBLB_STRATEGY\":", StringComparison.Ordinal) || prefix.StartsWith("\"OBLB_GROUP_STRATEGY\":", StringComparison.Ordinal))
                Assert.Equal("127.0.0.1:2881", await Create(http).ResolveAsync(CancellationToken.None));
            else await Assert.ThrowsAsync<NotSupportedException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("\"ADDRESS_LIST\":[", "\"ADDRESS_LIST\":[{},")]
        [InlineData("\"ADDRESS\":[", "\"OBLB_STRATEGY\":\"ROTATION\",\"ADDRESS\":[")]
        [InlineData("\"PORT\":2881", "\"WEIGHT\":2,\"PORT\":2881")]
        [InlineData("\"PROTOCOL\":\"TCP\"", "\"PROTOCOL\":\"TCPS\"")]
        public async Task GroupShapesAndAddressPoliciesAreValidated(string find, string replace)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881).Replace(find, replace));
            if (replace.Contains("OBLB_STRATEGY", StringComparison.Ordinal))
                Assert.Equal("127.0.0.1:2881", await Create(http).ResolveAsync(CancellationToken.None));
            else if (replace.Contains("{}", StringComparison.Ordinal))
                await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
            else await Assert.ThrowsAsync<NotSupportedException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Fact]
        public async Task OrderedDuplicateAddressesAndStringPortsArePreserved()
        {
            const string address = "{\"PROTOCOL\":\"tcp\",\"HOST\":\"[::1]\",\"PORT\":\"02881\"}";
            using var http = new OcpHttpFixture("{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"ADDRESS_LIST\":[{\"ADDRESS\":[" + address + "," + address + "]}]}}}");
            Assert.Equal("[::1]:2881,[::1]:2881", await Create(http).ResolveAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ParallelRequestsKeepTheirOwnResponseSnapshots()
        {
            using var http = new OcpHttpFixture(""); var resolver = Create(http); var index = 0;
            http.Respond = async (request, token) =>
            {
                var port = 1000 + Interlocked.Increment(ref index);
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OcpHttpFixture.Entry(port), Encoding.UTF8, "application/json") };
            };
            var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => resolver.ResolveAsync(CancellationToken.None)));
            Assert.Equal(32, results.Distinct().Count()); Assert.Equal(32, http.Requests.Count);
        }

        [Fact]
        public async Task HttpSendCancellationHonorsTheCallerToken()
        {
            using var http = new OcpHttpFixture(""); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            http.Respond = async (request, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); };
            using var stop = new CancellationTokenSource(); var resolving = Create(http).ResolveAsync(stop.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving); Assert.Single(http.Requests);
        }

        [Theory]
        [InlineData("\"PORT\":2881", "\"PORT\":\"2881,evil:1\"")]
        [InlineData("\"PORT\":2881", "\"PORT\":2881.0")]
        [InlineData("\"PORT\":2881", "\"PORT\":2e3")]
        [InlineData("\"PORT\":2881", "\"PORT\":0")]
        [InlineData("\"PORT\":2881", "\"PORT\":65536")]
        [InlineData("\"PORT\":2881", "\"PORT\":null")]
        [InlineData("\"HOST\":\"127.0.0.1\"", "\"HOST\":\"one,two\"")]
        [InlineData("\"HOST\":\"127.0.0.1\"", "\"HOST\":\"bad host\"")]
        [InlineData("\"PROTOCOL\":\"TCP\"", "\"PROTOCOL\":\"TCP\",\"PROTOCOL\":\"TCP\"")]
        public async Task InvalidAddressValuesAndDuplicateFieldsAreRejected(string find, string replace)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881).Replace(find, replace));
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ExactServiceBindingAndUnicodeQueryEscapingPreserveTrustBoundary()
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881).Replace("\"DESCRIPTION\":{", "\"DESCRIPTION\":{\"OBLB\":\"ON\",\"CONNECT_DATA\":{\"SERVICE_NAME\":\"tenant#one\"},"));
            await Assert.ThrowsAsync<NotSupportedException>(() => Create(http).ResolveAsync(CancellationToken.None));
            await Assert.ThrowsAsync<FormatException>(() => Create(http, service: "Tenant#one").ResolveAsync(CancellationToken.None));
            Assert.Equal("127.0.0.1:2881", await Create(http, service: "tenant#one").ResolveAsync(CancellationToken.None));
            http.Body = OcpHttpFixture.Entry(2881);
            await Assert.ThrowsAsync<FormatException>(() => Create(http, service: "tenant#one").ResolveAsync(CancellationToken.None));
            const string alias = "应用 &x=#?";
            http.Body = http.Body.Replace("\"APP\"", "\"" + alias + "\"");
            Assert.Equal("127.0.0.1:2881", await Create(http, alias).ResolveAsync(CancellationToken.None));
            Assert.Equal("?appName=" + Uri.EscapeDataString(alias), http.Requests.Last().Query);
        }

        [Theory]
        [InlineData(204)]
        [InlineData(301)]
        [InlineData(401)]
        [InlineData(500)]
        public async Task HttpFailureNeverRetriesOrReturnsEarlierAddresses(int status)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881)); var resolver = Create(http);
            Assert.Equal("127.0.0.1:2881", await resolver.ResolveAsync(CancellationToken.None));
            http.Status = (HttpStatusCode)status;
            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.ResolveAsync(CancellationToken.None));
            Assert.Equal(2, http.Requests.Count);
        }

        [Fact]
        public async Task RedirectedResponseIsRejectedAndClientOwnershipIsPreserved()
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881));
            var content = new TrackingContent(Encoding.UTF8.GetBytes(http.Body));
            http.Respond = (request, token) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
                Assert.True(request.Headers.CacheControl!.NoCache && request.Headers.CacheControl.NoStore);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.example/"), Content = content });
            };
            await Assert.ThrowsAsync<HttpRequestException>(() => Create(http).ResolveAsync(CancellationToken.None));
            Assert.True(content.Disposed);
            http.Respond = null;
            Assert.Equal("127.0.0.1:2881", await Create(http).ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("text/plain", null, false)]
        [InlineData("application/json", "utf-16", false)]
        [InlineData("application/json", "utf-8", true)]
        public async Task UnexpectedMediaCharsetOrEncodingIsRejected(string media, string? charset, bool encoded)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881));
            var content = new TrackingContent(Encoding.UTF8.GetBytes(http.Body));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(media) { CharSet = charset };
            if (encoded) content.Headers.ContentEncoding.Add("gzip");
            http.Respond = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
            Assert.True(content.Disposed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ActualBodyLimitAndDisposalApplyWithoutContentLength(bool oversized)
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881));
            var bytes = Encoding.UTF8.GetBytes(http.Body + new string(' ', 1024 * 1024 - Encoding.UTF8.GetByteCount(http.Body) + (oversized ? 1 : 0)));
            using var stream = new ReadStream(bytes);
            var content = new StreamContent(stream); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            http.Respond = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            if (oversized) await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
            else Assert.Equal("127.0.0.1:2881", await Create(http).ResolveAsync(CancellationToken.None));
            Assert.True(stream.Disposed); Assert.InRange(stream.ReadBytes, 1, 1024 * 1024 + 1);
        }

        [Fact]
        public async Task BodyReadCancellationDisposesTheResponseStream()
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881));
            using var stream = new ReadStream(null);
            var content = new StreamContent(stream); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            http.Respond = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            using var stop = new CancellationTokenSource();
            var resolving = Create(http).ResolveAsync(stop.Token);
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
            Assert.True(stream.Disposed);
        }

        [Fact]
        public async Task NodeDepthAndEndpointLimitsRejectExcessBeforePublication()
        {
            using var http = new OcpHttpFixture(OcpHttpFixture.Entry(2881)); var resolver = Create(http);
            http.Body = OcpHttpFixture.Entry(2881).Insert(1, "\"extra\":[" + string.Join(",", Enumerable.Repeat("0", 4082)) + "],");
            Assert.Equal("127.0.0.1:2881", await resolver.ResolveAsync(CancellationToken.None));
            http.Body = http.Body.Replace("\"extra\":[", "\"extra\":[0,");
            await Assert.ThrowsAsync<FormatException>(() => resolver.ResolveAsync(CancellationToken.None));
            http.Body = OcpHttpFixture.Entry(2881).Insert(1, "\"extra\":" + new string('[', 32) + "0" + new string(']', 32) + ",");
            await Assert.ThrowsAsync<FormatException>(() => resolver.ResolveAsync(CancellationToken.None));
            http.Body = OcpHttpFixture.Entry(2881).Insert(1, "\"extra\":" + new string('[', 14) + "0" + new string(']', 14) + ",");
            Assert.Equal("127.0.0.1:2881", await resolver.ResolveAsync(CancellationToken.None));
            http.Body = OcpHttpFixture.Entry(2881).Insert(1, "\"extra\":" + new string('[', 15) + "0" + new string(']', 15) + ",");
            await Assert.ThrowsAsync<FormatException>(() => resolver.ResolveAsync(CancellationToken.None));
            const string address = "{\"PROTOCOL\":\"TCP\",\"HOST\":\"h\",\"PORT\":1}";
            foreach (var count in new[] { 256, 257 })
            {
                http.Body = "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"ADDRESS_LIST\":[{\"ADDRESS\":[" + string.Join(",", Enumerable.Repeat(address, count)) + "]}]}}}";
                if (count == 256) Assert.Equal(256, (await resolver.ResolveAsync(CancellationToken.None)).Split(',').Length);
                else await Assert.ThrowsAsync<FormatException>(() => resolver.ResolveAsync(CancellationToken.None));
            }
            var host = string.Join(".", new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
            var longAddress = "{\"PROTOCOL\":\"TCP\",\"HOST\":\"" + host + "\",\"PORT\":65535}";
            foreach (var count in new[] { 252, 253 })
            {
                http.Body = "{\"data\":{\"NET_SERVICE_NAME\":\"APP\",\"DESCRIPTION\":{\"ADDRESS_LIST\":[{\"ADDRESS\":[" + string.Join(",", Enumerable.Repeat(longAddress, count)) + "]}]}}}";
                if (count == 252) Assert.Equal(65519, (await resolver.ResolveAsync(CancellationToken.None)).Length);
                else await Assert.ThrowsAsync<FormatException>(() => resolver.ResolveAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task MetadataSyntaxCorpusMatchesTheStandardJsonParser()
        {
            var corpus = new System.Collections.Generic.List<string> { "null", "true", "false", "{}", "[]", "[1,2,3]", "{\"a\":1}", "-0", "0.0", "1e-3", "1E+3", "\"\\uD800\"", "\"\\uD83D\\uDE00\"" };
            var random = new Random(1026);
            const string alphabet = "0123456789.eE+-truefalsn \t\r\n";
            for (var i = 0; i < 300; i++) corpus.Add(new string(Enumerable.Range(0, random.Next(1, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
            using var http = new OcpHttpFixture(""); var resolver = Create(http);
            foreach (var value in corpus)
            {
                http.Body = OcpHttpFixture.Entry(2881).Insert(1, "\"extra\":" + value + ",");
                var valid = true;
                try { using var document = System.Text.Json.JsonDocument.Parse(http.Body); }
                catch (System.Text.Json.JsonException) { valid = false; }
                if (valid) Assert.Equal("127.0.0.1:2881", await resolver.ResolveAsync(CancellationToken.None));
                else await Assert.ThrowsAsync<FormatException>(() => resolver.ResolveAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task DeclaredOversizedBodyIsRejectedBeforeReading()
        {
            using var http = new OcpHttpFixture(""); using var stream = new ReadStream(null);
            var content = new StreamContent(stream); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.ContentLength = 1024 * 1024 + 1;
            http.Respond = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None));
            Assert.False(stream.Started.Task.IsCompleted); Assert.True(stream.Disposed);
        }

        [Fact]
        public void ConstructorRejectsMissingClientServerAndNames()
        {
            using var http = new OcpHttpFixture(""); var origin = new Uri("https://ocp.example/");
            Assert.Throws<ArgumentNullException>(() => new OceanBaseOcpAddressResolver(null!, origin, "APP"));
            Assert.Throws<ArgumentNullException>(() => new OceanBaseOcpAddressResolver(http.Client, null!, "APP"));
            Assert.Throws<ArgumentException>(() => new OceanBaseOcpAddressResolver(http.Client, new Uri("relative", UriKind.Relative), "APP"));
            Assert.Throws<ArgumentException>(() => new OceanBaseOcpAddressResolver(http.Client, origin, " "));
            Assert.Throws<ArgumentException>(() => new OceanBaseOcpAddressResolver(http.Client, origin, "APP", " "));
        }

        [Theory]
        [InlineData("ff")]
        [InlineData("fffe4100")]
        [InlineData("c080")]
        public async Task InvalidUtf8IsRejectedBeforeJsonParsing(string hex)
        {
            using var http = new OcpHttpFixture(""); var content = new TrackingContent(Convert.FromHexString(hex));
            http.Respond = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            await Assert.ThrowsAsync<FormatException>(() => Create(http).ResolveAsync(CancellationToken.None)); Assert.True(content.Disposed);
        }

        [Theory]
        [InlineData("ftp://ocp.example/")]
        [InlineData("https://u:password@ocp.example/")]
        [InlineData("https://ocp.example/path")]
        [InlineData("https://ocp.example/?appName=other")]
        [InlineData("https://ocp.example/#fragment")]
        public void ConstructorRejectsAmbiguousOrCredentialBearingOrigins(string server)
        {
            using var http = new OcpHttpFixture("");
            Assert.Throws<ArgumentException>(() => new OceanBaseOcpAddressResolver(http.Client, new Uri(server), "APP"));
        }

        private sealed class TrackingContent : ByteArrayContent
        {
            internal TrackingContent(byte[] bytes) : base(bytes) { Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"); }
            internal bool Disposed { get; private set; }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        private sealed class ReadStream : Stream
        {
            private readonly MemoryStream? _inner;
            internal ReadStream(byte[]? bytes) { if (bytes != null) _inner = new MemoryStream(bytes); }
            internal bool Disposed { get; private set; }
            internal int ReadBytes { get; private set; }
            internal TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                if (_inner == null) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
                var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken); ReadBytes += read; return read;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { Disposed = true; if (disposing) _inner?.Dispose(); base.Dispose(disposing); }
        }

        private static IOceanBaseAddressResolver Create(OcpHttpFixture http, string alias = "APP", string? service = null, Uri? server = null)
        {
            var type = typeof(OceanBaseConnection).Assembly.GetType("OceanBaseConnector.OceanBaseOcpAddressResolver");
            Assert.NotNull(type);
            return Assert.IsAssignableFrom<IOceanBaseAddressResolver>(Activator.CreateInstance(type!, http.Client, server ?? new Uri("https://ocp.example/"), alias, service));
        }
        private static Task<string> Resolve(IOceanBaseAddressResolver resolver, bool asynchronous, CancellationToken token = default)
            => asynchronous ? resolver.ResolveAsync(token) : Task.FromResult(resolver.Resolve(token));
        private static Task Open(OceanBaseConnection connection, bool asynchronous)
        {
            if (asynchronous) return connection.OpenAsync();
            connection.Open(); return Task.CompletedTask;
        }
    }
}
