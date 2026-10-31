using System;
using System.Collections;
using OceanBaseConnector;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ConnectionStringBuilderTests
    {
        private const string ValidMinimal = "Server=ob1;User ID=root@sys";

        [Fact]
        public void MinimalConnectionStringBuildsWithDefaults()
        {
            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal);
            var options = builder.Build();

            Assert.Equal("ob1", options.Server);
            Assert.Equal(2881, options.Port);
            Assert.Equal("root@sys", options.UserId);
            Assert.Equal(string.Empty, options.Password);
            Assert.Equal(string.Empty, options.Database);
            Assert.Equal(OceanBaseConnector.OceanBaseTlsMode.VerifyFull, options.TlsMode);
            Assert.Equal(15, options.ConnectionTimeoutSeconds);
            Assert.Equal(30, options.CommandTimeoutSeconds);
            Assert.False(options.Pooling);
            Assert.Null(options.ExpectedMode);
            Assert.Equal(string.Empty, options.TlsServerName);
            Assert.Equal(0, options.MinPoolSize);
            Assert.Equal(100, options.MaxPoolSize);
            Assert.Equal(15, options.PoolWaitTimeoutSeconds);
            Assert.Equal(0, options.ConnectionIdleLifetimeSeconds);
            Assert.Equal(0, options.ConnectionLifetimeSeconds);
            Assert.False(options.UseBatchMultiSend);
            Assert.Equal(100, options.BatchMultiSendNumber);
        }

        [Fact]
        public void BatchMultiSendOptionsRoundTripAndFreezeForEveryEndpoint()
        {
            var builder = new OceanBaseConnectionStringBuilder(
                "Server=one,two;User ID=u;Use Batch Multi Send=true;Batch Multi Send Number=7");
            var frozen = builder.Build();

            Assert.True(builder.UseBatchMultiSend);
            Assert.Equal(7, builder.BatchMultiSendNumber);
            Assert.True(frozen.UseBatchMultiSend);
            Assert.Equal(7, frozen.BatchMultiSendNumber);
            Assert.Contains("Use Batch Multi Send=true", frozen.ToString(), StringComparison.Ordinal);
            Assert.Contains("Batch Multi Send Number=7", frozen.ToString(), StringComparison.Ordinal);

            builder.UseBatchMultiSend = false;
            builder.BatchMultiSendNumber = 2;
            foreach (var endpoint in frozen.Endpoints)
            {
                var selected = frozen.ForEndpoint(endpoint);
                Assert.True(selected.UseBatchMultiSend);
                Assert.Equal(7, selected.BatchMultiSendNumber);
            }
        }

        [Theory]
        [InlineData("Batch Multi Send Number=0")]
        [InlineData("Batch Multi Send Number=-1")]
        [InlineData("Batch Multi Send Number=abc")]
        public void InvalidBatchMultiSendNumberIsRejected(string option)
        {
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder(ValidMinimal + ";" + option));
        }

        [Fact]
        public void PoolCapacityAndLifetimeOptionsRoundTrip()
        {
            var options = new OceanBaseConnectionStringBuilder(
                ValidMinimal + ";Pooling=true;Min Pool Size=1;Max Pool Size=3;" +
                "Pool Wait Timeout=7;Connection Idle Lifetime=11;Connection Lifetime=13").Build();

            Assert.Equal(1, options.MinPoolSize);
            Assert.Equal(3, options.MaxPoolSize);
            Assert.Equal(7, options.PoolWaitTimeoutSeconds);
            Assert.Equal(11, options.ConnectionIdleLifetimeSeconds);
            Assert.Equal(13, options.ConnectionLifetimeSeconds);
        }

        [Theory]
        [InlineData("Min Pool Size=-1")]
        [InlineData("Max Pool Size=0")]
        [InlineData("Pool Wait Timeout=-1")]
        [InlineData("Connection Idle Lifetime=-1")]
        [InlineData("Connection Lifetime=-1")]
        public void InvalidPoolOptionsAreRejected(string option)
        {
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder(ValidMinimal + ";" + option));
        }

        [Fact]
        public void MinimumPoolSizeCannotExceedMaximumPoolSize()
        {
            Assert.Throws<ArgumentException>(() =>
                new OceanBaseConnectionStringBuilder(ValidMinimal + ";Min Pool Size=2;Max Pool Size=1").Build());
        }

        [Fact]
        public void EmptyConnectionStringIsAcceptedByBuilderButNotByBuild()
        {
            var builder = new OceanBaseConnectionStringBuilder();
            Assert.Equal(string.Empty, builder.ConnectionString);

            Assert.Throws<ArgumentException>(() => builder.Build());
        }

        [Fact]
        public void MissingUserIdIsRejectedByBuild()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1");
            Assert.Throws<ArgumentException>(() => builder.Build());
        }

        [Theory]
        [InlineData("Server=ob1;Port=0")]
        [InlineData("Server=ob1;Port=65536")]
        [InlineData("Server=ob1;Port=abc")]
        [InlineData("Server=ob1;Port=-1")]
        public void InvalidPortIsRejected(string connectionString)
        {
            Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder(connectionString));
        }

        [Fact]
        public void EmptyValueInConnectionStringRemovesTheKey()
        {
            // ADO.NET semantics: "Key=" clears the key rather than storing "".
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1;User ID=u;Port=;TLS Server Name=");
            Assert.Equal(OceanBaseConnectionStringBuilder.DefaultPort, builder.Port);
            Assert.Equal(string.Empty, builder.TlsServerName);

            // An empty Server therefore surfaces at Build as a missing value.
            var withoutServer = new OceanBaseConnectionStringBuilder("Server=;User ID=u");
            Assert.Throws<ArgumentException>(() => withoutServer.Build());
        }

        [Theory]
        [InlineData("Server=ob1;User ID=u;Connection Timeout=-1")]
        [InlineData("Server=ob1;User ID=u;Connection Timeout=abc")]
        [InlineData("Server=ob1;User ID=u;Command Timeout=-5")]
        [InlineData("Server=ob1;User ID=u;Command Timeout=1.5")]
        public void InvalidTimeoutsAreRejected(string connectionString)
        {
            Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder(connectionString));
        }

        [Theory]
        [InlineData("Server=ob1;Host=ob2")]
        [InlineData("Host=ob2;Server=ob1")]
        [InlineData("User ID=u;Uid=u2")]
        [InlineData("Pwd=p;Password=p2")]
        [InlineData("Password=p;Pwd=p2")]
        public void SynonymConflictsAreRejected(string connectionString)
        {
            var exception = Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder(connectionString));
            Assert.Contains("Conflicting", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SameKeywordTwiceTakesLastValue()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1;Server=ob2");
            Assert.Equal("ob2", builder.Server);
        }

        [Fact]
        public void SameSynonymTwiceTakesLastValue()
        {
            var builder = new OceanBaseConnectionStringBuilder("Host=ob1;Host=ob2");
            Assert.Equal("ob2", builder.Server);
        }

        [Fact]
        public void SslModeAliasUsesTlsMode()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1;User ID=u;SslMode=Disabled");
            Assert.Equal(OceanBaseTlsMode.Disabled, builder.TlsMode);
            Assert.Equal(OceanBaseTlsMode.Disabled, builder.Build().TlsMode);
        }

        [Fact]
        public void SslModeAndTlsModeConflictsAreRejected()
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                new OceanBaseConnectionStringBuilder("Server=ob1;User ID=u;SslMode=Disabled;TLS Mode=Required"));
            Assert.Contains("Conflicting", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Server=ob1;User ID=u;useServerPrepStmts=true")]
        [InlineData("Server=ob1;User ID=u;CompatibleMode=oracle")]
        public void UnknownKeysAreRejected(string connectionString)
        {
            var exception = Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder(connectionString));
            Assert.Contains("Unknown connection string keyword", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void EmptyServerValueViaIndexerIsRejected()
        {
            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal);
            Assert.Throws<ArgumentException>(() => builder["Server"] = " ");
        }

        [Fact]
        public void EmptyTlsServerNameViaIndexerIsRejectedWithHint()
        {
            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal);
            var exception = Assert.Throws<ArgumentException>(() => builder["TLS Server Name"] = "");
            Assert.Contains("remove the keyword", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void PasswordSerializesThroughConnectionString()
        {
            var builder = new OceanBaseConnectionStringBuilder(
                "Server=ob1;User ID=u;Password=secret;Database=test");
            var serialized = builder.ConnectionString;
            Assert.Contains("secret", serialized, StringComparison.Ordinal);

            var roundTripped = new OceanBaseConnectionStringBuilder(serialized);
            Assert.Equal("secret", roundTripped.Password);
        }

        [Fact]
        public void SanitizedStringHidesPasswordByDefault()
        {
            var builder = new OceanBaseConnectionStringBuilder(
                "Server=ob1;User ID=u;Password=secret;Database=test");

            var sanitized = builder.ToSanitizedConnectionString(includeSecrets: false);
            Assert.DoesNotContain("secret", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("Password", sanitized, StringComparison.Ordinal);
            Assert.Contains("Server=ob1", sanitized, StringComparison.Ordinal);

            var full = builder.ToSanitizedConnectionString(includeSecrets: true);
            Assert.Contains("secret", full, StringComparison.Ordinal);
        }

        [Fact]
        public void OptionsSnapshotIsRedactedAndImmutable()
        {
            var builder = new OceanBaseConnectionStringBuilder(
                "Server=ob1;User ID=u;Password=secret;Database=test");
            var options = builder.Build();

            Assert.DoesNotContain("secret", options.SanitizedConnectionString, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", options.ToString(), StringComparison.Ordinal);
            Assert.Equal("ob1:2881", options.Endpoint);

            // Mutating the builder after Build must not affect the snapshot.
            builder.Database = "other";
            Assert.Equal("test", options.Database);
        }

        [Fact]
        public void PasswordNeverLeaksIntoExceptionMessages()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1;User ID=u;Pwd=hunter2");

            var exception = Assert.Throws<ArgumentException>(
                () => builder["Password"] = "other");
            Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("other", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ConflictErrorDoesNotEchoValues()
        {
            var exception = Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder("Server=ob1;Host=ob2"));
            Assert.DoesNotContain("ob1", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ob2", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("TLS Mode=VerifyFull", OceanBaseTlsMode.VerifyFull)]
        [InlineData("TLS Mode=Required", OceanBaseTlsMode.Required)]
        [InlineData("TLS Mode=Disabled", OceanBaseTlsMode.Disabled)]
        [InlineData("TLS Mode=required", OceanBaseTlsMode.Required)]
        public void TlsModeParsesCaseInsensitively(string pair, OceanBaseTlsMode expected)
        {
            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal + ";" + pair);
            Assert.Equal(expected, builder.TlsMode);
            Assert.Equal(expected, builder.Build().TlsMode);
        }

        [Theory]
        [InlineData("TLS Mode=Preferred")]
        [InlineData("TLS Mode=verify_full")]
        [InlineData("TLS Mode=1")]
        public void InvalidTlsModeIsRejected(string pair)
        {
            Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder(ValidMinimal + ";" + pair));
        }

        [Fact]
        public void CertificateRevocationSettingRoundTrips()
        {
            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal + ";Check Certificate Revocation=true");
            Assert.True(builder.CheckCertificateRevocation);
            Assert.True(builder.Build().CheckCertificateRevocation);
            builder.CheckCertificateRevocation = false;
            Assert.False(builder.Build().CheckCertificateRevocation);
        }

        [Theory]
        [InlineData("ExpectedMode=MySQL", OceanBaseCompatibilityMode.MySql)]
        [InlineData("ExpectedMode=Oracle", OceanBaseCompatibilityMode.Oracle)]
        [InlineData("ExpectedMode=oracle", OceanBaseCompatibilityMode.Oracle)]
        public void ExpectedModeParses(string pair, OceanBaseCompatibilityMode expected)
        {
            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal + ";" + pair);
            Assert.Equal(expected, builder.ExpectedMode);
            Assert.Equal(expected, builder.Build().ExpectedMode);
        }

        [Theory]
        [InlineData("ExpectedMode=SqlServer")]
        [InlineData("ExpectedMode=mysql,oracle")]
        public void InvalidExpectedModeIsRejected(string pair)
        {
            Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder(ValidMinimal + ";" + pair));
        }

        [Fact]
        public void BooleansAreStrict()
        {
            Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder(ValidMinimal + ";Pooling=yes"));
            Assert.Throws<ArgumentException>(
                () => new OceanBaseConnectionStringBuilder(ValidMinimal + ";Pooling=1"));

            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal + ";Pooling=TRUE");
            Assert.True(builder.Pooling);
        }

        [Fact]
        public void PersistSecurityInfoDefaultsToFalseAndParses()
        {
            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal);
            Assert.False(builder.PersistSecurityInfo);

            builder = new OceanBaseConnectionStringBuilder(ValidMinimal + ";Persist Security Info=true");
            Assert.True(builder.PersistSecurityInfo);
        }

        [Fact]
        public void KeysAreCaseInsensitive()
        {
            var builder = new OceanBaseConnectionStringBuilder("server=ob1;user id=u;password=p;tls mode=Disabled");
            Assert.Equal("ob1", builder.Server);
            Assert.Equal("u", builder.UserId);
            Assert.Equal("p", builder.Password);
            Assert.Equal(OceanBaseTlsMode.Disabled, builder.TlsMode);
        }

        [Fact]
        public void TypedPropertiesRoundTrip()
        {
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "ob1",
                Port = 2888,
                UserId = "u@tenant",
                Password = "p",
                Database = "db",
                ConnectionTimeout = 7,
                CommandTimeout = 11,
                TlsMode = OceanBaseTlsMode.Required,
                TlsServerName = "ob1.example.com",
                PersistSecurityInfo = false,
                ExpectedMode = OceanBaseCompatibilityMode.MySql,
                Pooling = false,
            };

            var options = builder.Build();
            Assert.Equal("ob1", options.Server);
            Assert.Equal(2888, options.Port);
            Assert.Equal("u@tenant", options.UserId);
            Assert.Equal("p", options.Password);
            Assert.Equal("db", options.Database);
            Assert.Equal(7, options.ConnectionTimeoutSeconds);
            Assert.Equal(11, options.CommandTimeoutSeconds);
            Assert.Equal(OceanBaseTlsMode.Required, options.TlsMode);
            Assert.Equal("ob1.example.com", options.TlsServerName);
            Assert.Equal(OceanBaseCompatibilityMode.MySql, options.ExpectedMode);
        }

        [Fact]
        public void PasswordWithSpecialCharactersSurvivesRoundTrip()
        {
            const string password = "p;ss\"wo'rd=";
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "ob1",
                UserId = "u",
                Password = password,
            };

            var roundTripped = new OceanBaseConnectionStringBuilder(builder.ConnectionString);
            Assert.Equal(password, roundTripped.Password);
            Assert.DoesNotContain(password, builder.ToSanitizedConnectionString(false), StringComparison.Ordinal);
        }

        [Fact]
        public void RemoveDropsKeywordAndForgetsRawForm()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1;User ID=u;Pwd=p");
            Assert.True(builder.Remove("Pwd"));
            Assert.Equal(string.Empty, builder.Password);

            // After removal the synonym may be used again without conflict.
            builder["Password"] = "q";
            Assert.Equal("q", builder.Password);
        }

        [Fact]
        public void RemoveUnknownKeywordReturnsFalse()
        {
            var builder = new OceanBaseConnectionStringBuilder(ValidMinimal);
            Assert.False(builder.Remove("NoSuchKey"));
        }

        [Fact]
        public void ClearResetsConflictTracking()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1;User ID=u");
            Assert.Throws<ArgumentException>(() => builder["Host"] = "ob2");

            builder.Clear();
            builder.ConnectionString = "Host=ob3;Uid=u";
            Assert.Equal("ob3", builder.Server);
            Assert.Equal("u", builder.UserId);
        }

        [Fact]
        public void EmptyDatabaseAndPasswordAreAllowed()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob1;User ID=u;Password=;Database=");
            var options = builder.Build();
            Assert.Equal(string.Empty, options.Password);
            Assert.Equal(string.Empty, options.Database);
        }

        [Fact]
        public void EnumerationExposesCanonicalKeysOnly()
        {
            var builder = new OceanBaseConnectionStringBuilder("Host=ob1;Uid=u;Pwd=p");
            foreach (var entry in (System.Collections.IEnumerable)builder)
            {
                // Entries are DictionaryEntry on some runtimes and
                // KeyValuePair<string, object> on others; read Key reflectively.
                var key = (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!;
                Assert.DoesNotContain("Host", key, StringComparison.Ordinal);
                Assert.DoesNotContain("Uid", key, StringComparison.Ordinal);
                Assert.DoesNotContain("Pwd", key, StringComparison.Ordinal);
            }
        }
    }
}
