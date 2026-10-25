using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class TnsAddressResolverTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PublicResolverReadsStandardAndJavaWrappedAliases(bool wrapped)
        {
            var entry = "APP=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=first)(PORT=2881))(ADDRESS=(PROTOCOL=tcp)(HOST=::1)(PORT=2882))))";
            using var file = new TnsFileFixture(wrapped ? "(" + entry + ")" : entry);
            var resolver = Create(file);
            Assert.Equal("first:2881,[::1]:2882", resolver.Resolve(CancellationToken.None));
            Assert.Equal("first:2881,[::1]:2882", await resolver.ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PreCanceledResolutionDoesNotReadAMissingFile(bool asynchronous)
        {
            using var file = new TnsFileFixture(TnsFileFixture.Entry(2881));
            var resolver = Create(file); file.Dispose();
            using var stop = new CancellationTokenSource(); stop.Cancel();
            if (asynchronous) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(stop.Token));
            else Assert.ThrowsAny<OperationCanceledException>(() => resolver.Resolve(stop.Token));
        }

        [Fact]
        public async Task PortCannotInjectAnAdditionalCandidate()
        {
            using var file = new TnsFileFixture("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=first)(PORT=2881,second:2882)))");
            var resolver = Create(file);
            Assert.Throws<FormatException>(() => resolver.Resolve(CancellationToken.None));
            await Assert.ThrowsAsync<FormatException>(() => resolver.ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("")]
        [InlineData("APP=")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881))))")]
        [InlineData("APP=(DESCRIPTION=)")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS_LIST=))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=)(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=0)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=65536)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=+2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST='one,two')(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST='bad host')(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881)(PORT=2882)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(HOST=other)(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(PROTOCOL=TCP)(HOST=host)(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST='unterminated)(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST='host'trailing)(PORT=2881)))")]
        [InlineData("''=(X=x) APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1)))")]
        [InlineData("' '=(X=x) APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1)))")]
        [InlineData("OTHER=(''=x) APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1)))")]
        public async Task InvalidSyntaxAndAddressFieldsFailWithoutAResult(string content)
        {
            using var file = new TnsFileFixture(content); var resolver = Create(file);
            Assert.Throws<FormatException>(() => resolver.Resolve(CancellationToken.None));
            await Assert.ThrowsAsync<FormatException>(() => resolver.ResolveAsync(CancellationToken.None));
        }

        [Theory]
        [InlineData("APP=(DESCRIPTION_LIST=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881))))")]
        [InlineData("APP=(DESCRIPTION=(LOAD_BALANCE=ON)(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(FAILOVER=OFF)(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=host)(PORT=2881)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881)(WEIGHT=2)))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881)))(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=other)(PORT=2881))))")]
        [InlineData("APP=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881)))(ADDRESS=(PROTOCOL=TCP)(HOST=other)(PORT=2881)))")]
        [InlineData("IFILE=elsewhere.ob")]
        [InlineData("OTHER=(DESCRIPTION=(IFILE='secret-path')) APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881)))")]
        public async Task UnsupportedPoliciesProtocolsAndIncludesAreNeverIgnored(string content)
        {
            using var file = new TnsFileFixture(content); var resolver = Create(file);
            Assert.Throws<NotSupportedException>(() => resolver.Resolve(CancellationToken.None));
            await Assert.ThrowsAsync<NotSupportedException>(() => resolver.ResolveAsync(CancellationToken.None));
        }

        [Fact]
        public async Task AliasListsCommentsQuotesAndUnselectedPoliciesPreserveOrderedDuplicates()
        {
            const string address = "(ADDRESS=(PROTOCOL='tcp')(HOST=\"[::1]\")(PORT=02881))";
            using var file = new TnsFileFixture("\uFEFF # header\nOTHER=(DESCRIPTION=(LOAD_BALANCE=ON))\n(\"App\",alt=(DESCRIPTION= # comment\n(ADDRESS_LIST=" + address + address + "))) # end");
            foreach (var alias in new[] { "app", "ALT" })
            {
                var resolver = Create(file, alias);
                Assert.Equal("[::1]:2881,[::1]:2881", resolver.Resolve(CancellationToken.None));
                Assert.Equal("[::1]:2881,[::1]:2881", await resolver.ResolveAsync(CancellationToken.None));
            }
            Assert.Throws<FormatException>(() => Create(file, "missing").Resolve(CancellationToken.None));
        }

        [Theory]
        [InlineData("APP=(X=x) app=(X=x)")]
        [InlineData("OTHER,other=(X=x)")]
        [InlineData("APP=(X=x) (APP=(X=x))")]
        [InlineData("OTHER=(X=x) other=(X=x) APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=host)(PORT=2881)))")]
        public void DuplicateAliasesAreRejectedAcrossTheWholeFile(string content)
        {
            using var file = new TnsFileFixture(content);
            Assert.Throws<FormatException>(() => Create(file).Resolve(CancellationToken.None));
        }

        [Fact]
        public async Task ServiceMetadataRequiresAnExactExplicitExpectation()
        {
            using var file = new TnsFileFixture(TnsFileFixture.Entry(2881).Insert(TnsFileFixture.Entry(2881).Length - 1, "(CONNECT_DATA=(SERVICE_NAME='tenant#one'))"));
            Assert.Throws<NotSupportedException>(() => Create(file).Resolve(CancellationToken.None));
            Assert.Throws<FormatException>(() => Create(file, service: "Tenant#one").Resolve(CancellationToken.None));
            Assert.Equal("127.0.0.1:2881", await Create(file, service: "tenant#one").ResolveAsync(CancellationToken.None));
            file.Write(TnsFileFixture.Entry(2881));
            Assert.Throws<FormatException>(() => Create(file, service: "tenant#one").Resolve(CancellationToken.None));
        }

        [Theory]
        [InlineData("ff")]
        [InlineData("fffe4100")]
        [InlineData("c080")]
        public void InvalidUtf8CannotBeDecodedWithReplacement(string hex)
        {
            using var file = new TnsFileFixture(TnsFileFixture.Entry(2881));
            File.WriteAllBytes(file.Path, Convert.FromHexString(hex));
            Assert.Throws<FormatException>(() => Create(file).Resolve(CancellationToken.None));
        }

        [Fact]
        public void ByteNodeDepthAndCanonicalOutputLimitsHaveExplicitBoundaries()
        {
            var entry = TnsFileFixture.Entry(2881);
            using var file = new TnsFileFixture(entry + "\n#" + new string('x', 1024 * 1024 - Encoding.UTF8.GetByteCount(entry) - 2));
            var resolver = Create(file);
            Assert.Equal("127.0.0.1:2881", resolver.Resolve(CancellationToken.None));
            File.AppendAllText(file.Path, "x"); Assert.Throws<FormatException>(() => resolver.Resolve(CancellationToken.None));
            var direct = "APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1)))";
            var unused = string.Concat(Enumerable.Range(0, 2045).Select(i => "U" + i + "=(X=x)"));
            file.Write(direct + unused); Assert.Equal("h:1", resolver.Resolve(CancellationToken.None));
            file.Write(direct + unused + "EXTRA=(X=x)"); Assert.Throws<FormatException>(() => resolver.Resolve(CancellationToken.None));
            file.Write(direct + "OTHER=" + string.Concat(Enumerable.Repeat("(X=", 16)) + "x" + new string(')', 16));
            Assert.Equal("h:1", resolver.Resolve(CancellationToken.None));
            file.Write(direct + "OTHER=" + string.Concat(Enumerable.Repeat("(X=", 17)) + "x" + new string(')', 17));
            Assert.Throws<FormatException>(() => resolver.Resolve(CancellationToken.None));
            var host = string.Join(".", new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
            var address = "(ADDRESS=(PROTOCOL=TCP)(HOST=" + host + ")(PORT=65535))";
            file.Write("APP=(DESCRIPTION=(ADDRESS_LIST=" + string.Concat(Enumerable.Repeat(address, 252)) + "))");
            Assert.Equal(65519, resolver.Resolve(CancellationToken.None).Length);
            file.Write("APP=(DESCRIPTION=(ADDRESS_LIST=" + string.Concat(Enumerable.Repeat(address, 253)) + "))");
            Assert.Throws<FormatException>(() => resolver.Resolve(CancellationToken.None));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AddressCountLimitMatchesTheActualOpenPath(bool asynchronous)
        {
            await using var server = new CredentialServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle: false);
            var address = "(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=" + server.Port + "))";
            using var file = new TnsFileFixture("APP=(DESCRIPTION=(ADDRESS_LIST=" + string.Concat(Enumerable.Repeat(address, 256)) + "))");
            using var connection = new OceanBaseConnection("Server=localhost;Port=1;User ID=u;TLS Mode=Disabled;Connection Timeout=5") { AddressResolver = Create(file) };
            Assert.Equal(256, connection.AddressResolver.Resolve(CancellationToken.None).Split(',').Length);
            await Open(connection, asynchronous); connection.Close(); Assert.Equal(1, server.Accepted);
            file.Write("APP=(DESCRIPTION=(ADDRESS_LIST=" + string.Concat(Enumerable.Repeat(address, 257)) + "))");
            Assert.Throws<FormatException>(() => connection.AddressResolver.Resolve(CancellationToken.None));
            await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
            Assert.Equal(1, server.Accepted);
        }

        [Fact]
        public async Task ConcurrentReadersUseCompleteSnapshotsAndMissingFilesHaveNoFallback()
        {
            const string old = "APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=first)(PORT=1))(ADDRESS=(PROTOCOL=TCP)(HOST=second)(PORT=2)))";
            const string next = "APP=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=third)(PORT=3))(ADDRESS=(PROTOCOL=TCP)(HOST=fourth)(PORT=4)))";
            using var file = new TnsFileFixture(old); var resolver = Create(file);
            var readers = Enumerable.Range(0, 32).Select(async _ =>
            {
                for (var i = 0; i < 8; i++) Assert.Contains(await resolver.ResolveAsync(CancellationToken.None), new[] { "first:1,second:2", "third:3,fourth:4" });
            }).ToArray();
            for (var i = 0; i < 8; i++)
            {
                using var replacement = new TnsFileFixture(i % 2 == 0 ? next : old);
                File.Replace(replacement.Path, file.Path, null);
            }
            await Task.WhenAll(readers);
            File.Delete(file.Path); await Assert.ThrowsAsync<FileNotFoundException>(() => resolver.ResolveAsync(CancellationToken.None));
        }

        [Fact]
        public void DuplicateAliasInputIsRejectedWithoutMaterializingTheWholeList()
        {
            var content = string.Concat(Enumerable.Repeat("a,", 200000)) + "Z=(X=x)";
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<FormatException>(() => TnsAddressParser.Parse(content, "a", null, CancellationToken.None));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);
        }

        [Fact]
        public void AtomicPublisherWriteAccessDoesNotPreventReaders()
        {
            using var file = new TnsFileFixture(TnsFileFixture.Entry(2881));
            using var publisher = new FileStream(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            Assert.Equal("127.0.0.1:2881", Create(file).Resolve(CancellationToken.None));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PersistentSharingConflictsAreBoundedAndCancelable(bool cancel)
        {
            if (!OperatingSystem.IsWindows()) return; // Win32 sharing/lock violation retry contract.
            using var file = new TnsFileFixture(TnsFileFixture.Entry(2881)); var resolver = Create(file);
            using var publisher = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var stop = new CancellationTokenSource();
            if (cancel)
            {
                stop.CancelAfter(25);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(stop.Token));
            }
            else
            {
                var timer = Stopwatch.StartNew();
                var error = await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync(stop.Token));
                Assert.Equal(unchecked((int)0x80070020), error.HResult);
                Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(90));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MissingFilesHaveBoundedCancelableRetriesWithoutCachedAddresses(bool cancel)
        {
            using var file = new TnsFileFixture(TnsFileFixture.Entry(2881)); var resolver = Create(file);
            Assert.Equal("127.0.0.1:2881", resolver.Resolve(CancellationToken.None)); File.Delete(file.Path);
            using var stop = new CancellationTokenSource();
            if (cancel)
            {
                stop.CancelAfter(25);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(stop.Token));
            }
            else
            {
                var timer = Stopwatch.StartNew();
                await Assert.ThrowsAsync<FileNotFoundException>(() => resolver.ResolveAsync(stop.Token));
                Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(90));
            }
        }

        [Theory]
        [InlineData(null, "APP", null)]
        [InlineData("", "APP", null)]
        [InlineData("file.ob", null, null)]
        [InlineData("file.ob", " ", null)]
        [InlineData("file.ob", "APP", " ")]
        public void ConstructorRejectsMissingConfiguration(string? path, string? alias, string? service)
            => Assert.Throws<ArgumentException>(() => new OceanBaseTnsAddressResolver(path!, alias!, service));

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task EveryOpenReadsTheFileBeforeReusingOrReplacingSessions(bool asynchronous, bool oracle, bool pooled)
        {
            var greeting = ProtocolFixture.Load("initial-handshake-oceanbase").Payload;
            await using var first = new CredentialServer(greeting, oracle: oracle, sessionStateOk: oracle ? TextCommandTests.TimeZoneOk("+00:00") : null);
            await using var second = new CredentialServer(greeting, oracle: oracle, sessionStateOk: oracle ? TextCommandTests.TimeZoneOk("+00:00") : null);
            using var file = new TnsFileFixture(TnsFileFixture.Entry(first.Port));
            var resolver = Create(file);
            using var connection = new OceanBaseConnection($"Server=localhost;Port=1;User ID=u;TLS Mode=Disabled;Pooling={pooled};Max Pool Size=1;Connection Timeout=5") { AddressResolver = resolver };
            try
            {
                await Open(connection, asynchronous); var original = connection.GetOpenSession(); connection.Close();
                file.Write("APP=(DESCRIPTION=broken)");
                await Assert.ThrowsAsync<OceanBaseException>(() => Open(connection, asynchronous));
                Assert.Equal(ConnectionState.Closed, connection.State);
                file.Write(TnsFileFixture.Entry(first.Port));
                await Open(connection, asynchronous);
                if (pooled) Assert.Same(original, connection.GetOpenSession());
                connection.Close(); file.Write(TnsFileFixture.Entry(second.Port));
                await Open(connection, asynchronous);
                Assert.Equal("127.0.0.1:" + second.Port, connection.GetOpenSession().Endpoint);
                Assert.Equal(SessionState.Closed, original.State);
                using var command = connection.CreateCommand(); command.CommandText = "select resolved";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        private static IOceanBaseAddressResolver Create(TnsFileFixture file, string alias = "app", string? service = null)
        {
            var type = typeof(OceanBaseConnection).Assembly.GetType("OceanBaseConnector.OceanBaseTnsAddressResolver");
            Assert.NotNull(type);
            return Assert.IsAssignableFrom<IOceanBaseAddressResolver>(Activator.CreateInstance(type!, file.Path, alias, service));
        }

        private static Task Open(OceanBaseConnection connection, bool asynchronous)
        {
            if (asynchronous) return connection.OpenAsync();
            connection.Open(); return Task.CompletedTask;
        }
    }
}
