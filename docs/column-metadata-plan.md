# Column catalog metadata implementation plan

Approved scope: [investigation and contract](column-metadata-investigation.md).
Implement in the current checkout on codex/column-catalog-metadata. No server,
Java, protocol or EF changes; no DDL parsing or identity-option recovery.

1. Add public GetSchema/GetSchemaAsync loopback tests for typed appended fields,
   tri-state flags, literal/expression ambiguity, complete type declarations,
   restrictions, server errors and cancellation. Observe the missing-feature
   failure before implementing. Reuse the existing protocol fixtures.
2. Extend Columns only. Keep its first twelve fields and row/filter semantics.
   Read public directory components and normalize them in shared C# code used
   by both interfaces. Restrict unknown-field fallback to optional metadata;
   preserve permission and operational exceptions.
3. Extend the built-asset public consumer matrix and opt-in integration schema
   tests. Keep synthetic and real-server evidence separate. Never log secrets.
4. Document the actual field types, state matrix, conservative default/type
   limitations and a public API consumer example in docs/schema.md.
5. Build all solution assets, run the full solution regression suite, inspect
   the diff and obtain a fresh independent code review. Resolve findings,
   repeat affected checks, then commit the complete change without publishing.

Review focus: Oracle NUMBER(*,s)/FLOAT/CHAR BYTE versus CHAR semantics; raw versus
SQL defaults; unknown flags never becoming false; quoted exact restrictions;
fallback errors never hiding permissions, cancellation or malformed results.

## Execution record

- Baseline: 083ab60c7bf44411a2c2b14e0758b2b2c222de27; only the investigation
  document was untracked. The prior targeted baseline passed 44 unit and 12
  consumer tests. User authorized implementation, verification, review and commit.
- Execution uses the approved current workspace and a dedicated branch; no new
  worktree or extra approval cycle is needed for this bounded change.
- Implemented eleven appended fields with nullable Boolean presence flags,
  conservative public-catalog normalization and one legacy-projection retry for
  known missing-column errors. No Java, server, protocol, ExpectedMode, EF or
  dependency changes were made.
- Release solution build succeeded for netstandard2.0, netstandard2.1, net8.0,
  net9.0 and net10.0 with zero warnings and zero errors.
- Targeted public schema/column unit tests: 155 passed, zero failed. The public
  consumer example in schema.md also compiled with zero warnings/errors.
- Full Release solution regression completed successfully: 10,280 unit tests,
  4,093 built-asset consumer tests, 14 dependency-injection tests and 43
  integration configuration tests passed; zero failures. The 318 live database
  cases were skipped under the existing opt-in configuration, not accepted as
  server evidence. TRX files are in ignored TestResults/column-metadata/.
- Fresh independent code review found no outstanding critical or important
  findings. Its Oracle stored-generation finding was fixed and covered: a NO
  virtual flag cannot establish an ordinary column or a default clause.
- Both configured safe-profile read-only connection attempts failed before
  directory SQL with OceanBaseException (inner ProtocolException). No live
  server version, query result or DDL acceptance was established; no writes or
  DDL were attempted. Source inspection is not server verification.
- No package version bump, publication or push is included. The resulting Git
  commit is the consumer handoff; the published 0.2.0 package is unchanged.
