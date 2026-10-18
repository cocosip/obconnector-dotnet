using System;
using System.IO;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ProtocolFixtureTests
    {
        [Fact]
        public void SampleErrFixtureParsesWithProvenance()
        {
            var fixture = ProtocolFixture.Load("err-packet-handshake-failure");

            Assert.Equal("hand-crafted", fixture.Source);
            Assert.NotNull(fixture.Description);
            Assert.NotNull(fixture.Redaction);

            // 0xFF header, error code 1045 little-endian, '#', "28000".
            Assert.Equal(0xFF, fixture.Payload[0]);
            Assert.Equal(0x15, fixture.Payload[1]);
            Assert.Equal(0x04, fixture.Payload[2]);
            Assert.Equal((byte)'#', fixture.Payload[3]);
            Assert.Equal("28000", System.Text.Encoding.ASCII.GetString(fixture.Payload, 4, 5));

            Assert.Equal(1045, fixture.Expectation.GetProperty("errorCode").GetInt32());
            Assert.Equal("28000", fixture.Expectation.GetProperty("sqlState").GetString());
        }

        [Fact]
        public void FixtureNameMustMatchFileName()
        {
            var path = Path.Combine(Path.GetTempPath(), "mismatched-name.json");
            File.WriteAllText(path, MinimalFixtureJson(name: "other-name"));

            try
            {
                Assert.Throws<InvalidDataException>(() => ProtocolFixture.LoadFile(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("name")]
        [InlineData("description")]
        [InlineData("source")]
        [InlineData("redaction")]
        [InlineData("payloadBase64")]
        [InlineData("expectation")]
        public void MissingRequiredFieldsAreRejected(string missing)
        {
            var path = Path.Combine(Path.GetTempPath(), $"missing-{missing}.json");
            File.WriteAllText(path, MinimalFixtureJson(missingProperty: missing));

            try
            {
                Assert.Throws<InvalidDataException>(() => ProtocolFixture.LoadFile(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CapturedFixtureRequiresProvenanceFields()
        {
            var path = Path.Combine(Path.GetTempPath(), "captured-no-version.json");
            File.WriteAllText(path, MinimalFixtureJson(name: "captured-no-version", source: "captured"));

            try
            {
                Assert.Throws<InvalidDataException>(() => ProtocolFixture.LoadFile(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void InvalidBase64PayloadIsRejected()
        {
            var path = Path.Combine(Path.GetTempPath(), "bad-base64.json");
            File.WriteAllText(path, MinimalFixtureJson(name: "bad-base64", payloadBase64: "!!not-base64!!"));

            try
            {
                Assert.Throws<InvalidDataException>(() => ProtocolFixture.LoadFile(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string MinimalFixtureJson(
            string? missingProperty = null,
            string name = "fixture",
            string source = "hand-crafted",
            string payloadBase64 = "AAAA")
        {
            var properties = new (string Key, string Value)[]
            {
                ("name", $"\"{name}\""),
                ("description", "\"test fixture\""),
                ("source", $"\"{source}\""),
                ("redaction", "\"synthetic\""),
                ("payloadBase64", $"\"{payloadBase64}\""),
                ("expectation", "{}"),
            };

            var writer = new System.Text.StringBuilder();
            writer.Append('{');
            var first = true;
            foreach (var (key, value) in properties)
            {
                if (key == missingProperty)
                {
                    continue;
                }

                if (!first)
                {
                    writer.Append(',');
                }

                writer.Append($"\"{key}\":{value}");
                first = false;
            }

            writer.Append('}');
            return writer.ToString();
        }
    }
}
