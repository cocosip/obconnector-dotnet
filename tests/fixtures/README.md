# Protocol fixtures

Binary protocol fixtures for unit tests that must not depend on a live
OceanBase server. Fixtures live in version control next to the tests and are
loaded by `OceanBaseConnector.UnitTests` via `ProtocolFixture.Load*`.

## Format rules

Each fixture is a single JSON file in this directory tree with:

| Field | Required | Meaning |
| --- | --- | --- |
| `name` | yes | Stable identifier, must equal the file name without `.json`. |
| `description` | yes | What protocol scenario the bytes represent. |
| `source` | yes | Where the bytes come from: `hand-crafted` (synthetic, built from the protocol description) or `captured` (from a real session; then `reference`, `serverVersion`, `endpointType` and `mode` are mandatory). |
| `reference` | captured only | Commit URL or document section the bytes were verified against. |
| `serverVersion` | captured only | Exact server or OBProxy version, e.g. `OceanBase 4.2.1`. |
| `endpointType` | captured only | `observer` or `obproxy`. |
| `mode` | captured only | `mysql` or `oracle` compatibility mode of the session. |
| `redaction` | yes | Statement of what was scrubbed; captured payloads must never contain credentials, real host names, or parameter values. |
| `payloadBase64` | yes | The raw bytes under test. |
| `expectation` | yes | Free-form object describing the expected parse result; its shape is defined per fixture family by the test that consumes it. |

Rules:

- No real credentials, certificates, host names, parameter values or captured
  SQL text in any fixture.
- Captured payloads are byte-identical to what the wire carried, with
  authentication data replaced by synthetic bytes of the same length; the
  `redaction` field must describe the replacement.
- A fixture without complete provenance fields is invalid; the loader
  rejects it rather than letting the test pass on partial metadata.
