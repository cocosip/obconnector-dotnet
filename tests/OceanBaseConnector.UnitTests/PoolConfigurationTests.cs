using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PoolConfigurationTests
    {
        public static IEnumerable<object[]> Options()
        {
            yield return new object[] { "Server", "other.invalid" };
            yield return new object[] { "Port", "2882" };
            yield return new object[] { "User ID", "other" };
            yield return new object[] { "Password", "other;secret=\"p\"" };
            yield return new object[] { "Database", "other" };
            yield return new object[] { "Connection Timeout", "7" };
            yield return new object[] { "Command Timeout", "7" };
            yield return new object[] { "Connect Retry Count", "2" };
            yield return new object[] { "Connect Retry Interval", "3" };
            yield return new object[] { "Slow Query Threshold", "7" };
            yield return new object[] { "TLS Mode", "Required" };
            yield return new object[] { "TLS Server Name", "other.invalid" };
            yield return new object[] { "Check Certificate Revocation", "true" };
            yield return new object[] { "Persist Security Info", "true" };
            yield return new object[] { "ExpectedMode", "MySql" };
            yield return new object[] { "Pooling", "true" };
            yield return new object[] { "Min Pool Size", "1" };
            yield return new object[] { "Max Pool Size", "2" };
            yield return new object[] { "Pool Wait Timeout", "7" };
            yield return new object[] { "Connection Idle Lifetime", "7" };
            yield return new object[] { "Connection Lifetime", "7" };
            yield return new object[] { "Statement Cache Size", "2" };
            yield return new object[] { "Server RSA Public Key", "other-key" };
            yield return new object[] { "Allow Public Key Retrieval", "true" };
            yield return new object[] { "Use OB20", "true" };
            yield return new object[] { "Use Compression", "true" };
            yield return new object[] { "Enable Full Link Trace", "true" };
            yield return new object[] { "Use Oracle Prepare Execute", "true" };
            yield return new object[] { "Use Lob Locators", "true" };
            yield return new object[] { "Allow Multi Statements", "true" };
            yield return new object[] { "Allow Local Infile", "true" };
            yield return new object[] { "Load Balance", "RoundRobin" };
            yield return new object[] { "Service Principal Name", "oceanbase/other.invalid@REALM" };
            yield return new object[] { "Treat Tiny As Boolean", "false" };
            yield return new object[] { "Use Affected Rows", "true" };
        }

        [Fact]
        public void OptionIsolationCasesCoverEveryDeclaredKeyword()
        {
            var declared = typeof(OceanBaseConnectionStringBuilder).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).OrderBy(x => x);
            Assert.Equal(declared, Options().Select(x => (string)x[0]).OrderBy(x => x));
        }

        [Theory]
        [MemberData(nameof(Options))]
        public void EveryOptionRemainsPartOfPoolIdentity(string key, string value)
        {
            var first = new OceanBaseConnectionStringBuilder("Server=" + Guid.NewGuid().ToString("N") + ".invalid");
            var second = new OceanBaseConnectionStringBuilder(first.ConnectionString) { [key] = value };
            var a = PhysicalSessionPoolRegistry.Get(first.ConnectionString);
            var b = PhysicalSessionPoolRegistry.Get(second.ConnectionString);
            try { Assert.NotSame(a, b); }
            finally
            {
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(first.ConnectionString, a);
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(second.ConnectionString, b);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("Password2=\"\";Password10=ten")]
        [InlineData("Server=first,second;User ID=Case;Password=\"p;Password2=q\";Password2=\"q;Database=db\"")]
        [InlineData("Server=[::1]:2882;ExpectedMode=oracle;Connection Timeout=7;Pool Wait Timeout=0")]
        public void CanonicalIdentityCanBeReparsedWithoutChangingPublicSerialization(string configuration)
        {
            var builder = new OceanBaseConnectionStringBuilder(configuration);
            var original = builder.ConnectionString;
            var canonical = builder.ToPoolConfiguration();
            Assert.Equal(canonical, new OceanBaseConnectionStringBuilder(canonical).ToPoolConfiguration());
            Assert.Equal(original, builder.ConnectionString);
            if (builder.ContainsKey("Password2"))
                Assert.Equal(builder["Password2"], new OceanBaseConnectionStringBuilder(canonical)["Password2"]);
        }

        [Theory]
        [InlineData("Password2=\"\"", "")]
        [InlineData("Password2=two", "Password2=TWO")]
        [InlineData("Password2=two;Password10=ten", "Password2=ten;Password10=two")]
        [InlineData("User ID=Case", "User ID=case")]
        [InlineData("Database=Case", "Database=case")]
        [InlineData("Server=first,second", "Server=second,first")]
        [InlineData("Server=First", "Server=first")]
        [InlineData("Connection Timeout=7", "Connection Timeout=7;Pool Wait Timeout=15")]
        [InlineData("ExpectedMode=MySql", "")]
        [InlineData("Password=\"p;Database=a\";Database=b", "Password=p;Database=\"a;Database=b\"")]
        public void TextSecretsOptionalFactorsAndPoliciesDoNotCollide(string first, string second)
        {
            Assert.NotEqual(new OceanBaseConnectionStringBuilder(first).ToPoolConfiguration(),
                new OceanBaseConnectionStringBuilder(second).ToPoolConfiguration());
        }

        [Fact]
        public void EquivalentConfigurationClearsAndRemovesOriginalEmptyPool()
        {
            var first = "Server=" + Guid.NewGuid().ToString("N") + ".invalid;Pooling=true";
            var second = first + ";Port=2881";
            var original = PhysicalSessionPoolRegistry.Get(first);
            PhysicalSessionPool? replacement = null;
            try
            {
                PhysicalSessionPoolRegistry.Clear(second, null, null, null);
                Assert.Throws<ObjectDisposedException>(original.BeginRent);
                replacement = PhysicalSessionPoolRegistry.Get(first);
                Assert.NotSame(original, replacement);
            }
            finally
            {
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(first, original);
                if (replacement != null) PhysicalSessionPoolRegistry.ReleaseIfEmpty(first, replacement);
            }
        }

        [Theory]
        [InlineData("User ID=u;Pooling=true", "Pooling=true;User ID=u")]
        [InlineData("Pooling=true", "Pooling=true;Connect Retry Count=0;Connect Retry Interval=1")]
        [InlineData("Pooling=true", "Pooling=TRUE")]
        [InlineData("Pooling=true", "Pooling=true;Port=2881")]
        [InlineData("Pooling=true", "Pooling=true;Max Pool Size=0100")]
        [InlineData("Pooling=true", "Pooling=true;TLS Mode=verifyfull;Load Balance=sequential")]
        [InlineData("Pooling=true;Connection Timeout=7", "Pooling=true;Connection Timeout=007;Pool Wait Timeout=7")]
        [InlineData("Password2=two;Password10=ten", "password10=ten;password2=two")]
        [InlineData("User ID=u;Password=p", "Uid=u;Pwd=p")]
        public void EquivalentConfigurationsShareAnOrdinaryPool(string first, string second)
        {
            var prefix = "Server=" + Guid.NewGuid().ToString("N") + ".invalid;";
            // Use the same serialization boundary as the public connection setter.
            first = new OceanBaseConnectionStringBuilder(prefix + first).ConnectionString;
            second = new OceanBaseConnectionStringBuilder(prefix + second).ConnectionString;
            var a = PhysicalSessionPoolRegistry.Get(first);
            var b = PhysicalSessionPoolRegistry.Get(second);
            try { Assert.Same(a, b); }
            finally
            {
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(first, a);
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(second, b);
            }
        }

        [Fact]
        public void EquivalentConfigurationReleasesOriginalRegistryEntry()
        {
            var first = "Server=" + Guid.NewGuid().ToString("N") + ".invalid;Pooling=true";
            var second = first + ";Port=2881";
            var original = PhysicalSessionPoolRegistry.Get(first);
            try { Assert.True(PhysicalSessionPoolRegistry.ReleaseIfEmpty(second, original)); }
            finally { PhysicalSessionPoolRegistry.ReleaseIfEmpty(first, original); }
        }

        [Fact]
        public void EquivalentConfigurationsStillIsolateReferencePoliciesAndPrivateSources()
        {
            var configuration = "Server=" + Guid.NewGuid().ToString("N") + ".invalid;User ID=u;Pooling=true";
            var equivalent = configuration + ";Port=2881";
            var first = new Policy(); var second = new Policy();
            RemoteCertificateValidationCallback a = first.Validate, b = first.Validate;
            Assert.Equal(a, b);
            Assert.NotSame(a, b);
            var pools = new[]
            {
                PhysicalSessionPoolRegistry.Get(configuration, first, a, credentialProvider: first),
                PhysicalSessionPoolRegistry.Get(equivalent, second, a, credentialProvider: first),
                PhysicalSessionPoolRegistry.Get(equivalent, first, b, credentialProvider: first),
                PhysicalSessionPoolRegistry.Get(equivalent, first, a, credentialProvider: second),
            };
            try
            {
                Assert.Equal(4, pools.Distinct().Count());
                Assert.Same(pools[0], PhysicalSessionPoolRegistry.Get(equivalent, first, a, credentialProvider: first));
                using var sourceA = new OceanBaseDataSourceBuilder(configuration).Build();
                using var sourceB = new OceanBaseDataSourceBuilder(equivalent).Build();
                Assert.NotSame(sourceA.Pool, sourceB.Pool);
                Assert.All(pools, pool => { Assert.NotSame(pool, sourceA.Pool); Assert.NotSame(pool, sourceB.Pool); });
            }
            finally
            {
                foreach (var pool in pools) PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, pool);
            }
        }

        private sealed class Policy : IOceanBaseAddressResolver, IOceanBaseCredentialProvider
        {
            public override bool Equals(object? obj) => obj is Policy;
            public override int GetHashCode() => 0;
            public string Resolve(CancellationToken token) => throw new NotSupportedException();
            public Task<string> ResolveAsync(CancellationToken token) => throw new NotSupportedException();
            public OceanBaseCredential GetCredential(OceanBaseCredentialContext context, CancellationToken token) => throw new NotSupportedException();
            public Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context, CancellationToken token) => throw new NotSupportedException();
            internal bool Validate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
                System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors) => true;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedOpenReleasesTheOriginalEmptyFactorIdentity(bool asynchronous)
        {
            var configuration = "Server=" + Guid.NewGuid().ToString("N") + ".invalid;User ID=u;Pooling=true;Password2=\"\"";
            var resolver = new Policy();
            var pool = PhysicalSessionPoolRegistry.Get(configuration, resolver);
            using var connection = new OceanBaseConnection(configuration) { AddressResolver = resolver };
            try
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
                Assert.IsType<NotSupportedException>(error.InnerException);
                Assert.Throws<ObjectDisposedException>(pool.BeginRent);
            }
            finally { connection.Close(); pool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, pool); }
        }
    }
}
