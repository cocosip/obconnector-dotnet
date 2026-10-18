using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OceanBaseConnector.UnitTests
{
    /// <summary>
    /// Loads protocol fixtures from tests/fixtures. The format is documented
    /// in tests/fixtures/README.md: provenance fields are mandatory for
    /// captured payloads and the loader rejects fixtures with incomplete
    /// metadata instead of letting tests pass on partial provenance.
    /// </summary>
    public sealed class ProtocolFixture
    {
        public ProtocolFixture(
            string name,
            string description,
            string source,
            string? reference,
            string? serverVersion,
            string? endpointType,
            string? mode,
            string redaction,
            byte[] payload,
            JsonElement expectation)
        {
            Name = name;
            Description = description;
            Source = source;
            Reference = reference;
            ServerVersion = serverVersion;
            EndpointType = endpointType;
            Mode = mode;
            Redaction = redaction;
            Payload = payload;
            Expectation = expectation;
        }

        public string Name { get; }

        public string Description { get; }

        public string Source { get; }

        public string? Reference { get; }

        public string? ServerVersion { get; }

        public string? EndpointType { get; }

        public string? Mode { get; }

        public string Redaction { get; }

        public byte[] Payload { get; }

        public JsonElement Expectation { get; }

        public static ProtocolFixture Load(string name, string family = "protocol")
        {
            var path = Path.Combine(FindFixturesDirectory(), family, name + ".json");
            return LoadFile(path);
        }

        public static ProtocolFixture LoadFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Fixture file not found.", path);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            string RequiredString(string property)
            {
                if (!root.TryGetProperty(property, out var element) ||
                    element.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(element.GetString()))
                {
                    throw new InvalidDataException($"Fixture '{path}' is missing required string property '{property}'.");
                }

                return element.GetString()!;
            }

            var name = RequiredString("name");
            var expectedName = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(name, expectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Fixture name '{name}' must equal the file name '{expectedName}'.");
            }

            var source = RequiredString("source");
            if (source != "hand-crafted" && source != "captured")
            {
                throw new InvalidDataException(
                    $"Fixture '{path}' has invalid source '{source}'; expected 'hand-crafted' or 'captured'.");
            }

            if (source == "captured")
            {
                foreach (var property in new[] { "reference", "serverVersion", "endpointType", "mode" })
                {
                    if (!root.TryGetProperty(property, out var element) ||
                        element.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(element.GetString()))
                    {
                        throw new InvalidDataException(
                            $"Captured fixture '{path}' is missing provenance property '{property}'.");
                    }
                }

                var endpointType = root.GetProperty("endpointType").GetString();
                if (endpointType != "observer" && endpointType != "obproxy")
                {
                    throw new InvalidDataException(
                        $"Fixture '{path}' has invalid endpointType '{endpointType}'; expected 'observer' or 'obproxy'.");
                }

                var mode = root.GetProperty("mode").GetString();
                if (mode != "mysql" && mode != "oracle")
                {
                    throw new InvalidDataException(
                        $"Fixture '{path}' has invalid mode '{mode}'; expected 'mysql' or 'oracle'.");
                }
            }

            if (!root.TryGetProperty("expectation", out var expectation) || expectation.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Fixture '{path}' is missing the expectation object.");
            }

            var payloadBase64 = RequiredString("payloadBase64");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(payloadBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException($"Fixture '{path}' has invalid base64 payload.", exception);
            }

            return new ProtocolFixture(
                name,
                RequiredString("description"),
                source,
                GetStringOrNull("reference"),
                GetStringOrNull("serverVersion"),
                GetStringOrNull("endpointType"),
                GetStringOrNull("mode"),
                RequiredString("redaction"),
                payload,
                expectation.Clone());

            string? GetStringOrNull(string property)
            {
                return root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;
            }
        }

        private static string FindFixturesDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && directory is not null; i++)
            {
                var candidate = Path.Combine(directory.FullName, "fixtures");
                if (Directory.Exists(Path.Combine(candidate, "protocol")))
                {
                    return candidate;
                }

                var repositoryCandidate = Path.Combine(directory.FullName, "tests", "fixtures");
                if (Directory.Exists(Path.Combine(repositoryCandidate, "protocol")))
                {
                    return repositoryCandidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"Could not locate the fixtures directory above '{AppContext.BaseDirectory}'.");
        }
    }
}
