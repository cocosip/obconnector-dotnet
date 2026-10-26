# Client SQL and parameter character sets

`OceanBaseConnectionStringBuilder.CharacterSet` / `Character Set` selects the encoding of
SQL, database-selection requests and ordinary character parameters. `CharacterSet` is a keyword
alias. Names are case insensitive and canonicalized; `utf8` means `utf8mb3`.

| Accepted names | Input boundary |
| --- | --- |
| `utf8mb4` (default) | Existing strict UTF-8 behavior, including supplementary characters |
| `utf8mb3`, `ascii`, `latin1` | No supplementary characters in mb3; ASCII is 7-bit; MySQL latin1 uses CP1252 |
| `gbk`, `gb18030`, `gb18030_2022` | Existing strict server mappings; GB18030 uses the 2005 mapping unless 2022 is selected |
| `dec8`, `tis620`, `sjis`, `big5`, `hkscs`, `hkscs31` | Existing pinned server mappings, with ASCII syntax preserved for input |

UTF-16/LE, binary and unknown names fail configuration validation before networking. Encoders
reject malformed Unicode, unmappable characters and aliases that do not decode to the original
text. They never substitute `?`. SJIS backslash is sent as ASCII `5C`, while non-ASCII characters
retain their strict server mapping. GBK's CP936-only euro alias and HKSCS's lossy U+2593 alias are
rejected. This option does not select a different result decoder or make unsupported types legal.

```csharp
using var connection = new OceanBaseConnection(
    "Server=db.example;User ID=u;ExpectedMode=Oracle;Character Set=gbk");
await connection.OpenAsync();
using var command = connection.CreateCommand();
command.CommandText = "SELECT :value FROM dual"; // Oracle mode example
command.Parameters.Add(new OceanBaseParameter { ParameterName = "value", Value = "中文" });
var value = await command.ExecuteScalarAsync();
```

## Initialization and pooling

The omitted option and explicit `utf8mb4` use the existing handshake and command behavior; they
share pool identity. They do not enable additional session charset management. Authentication,
user/database handshake fields and initial identity queries remain UTF-8 (handshake collation45).

A non-default name enables managed input encoding. After authentication and identity confirmation,
Open executes `SET NAMES <canonical name>`, then `SET character_set_results=utf8mb4`, and reads
client/connection/results/collation state. Both input names must match the configured family,
results must be utf8mb4, and collation must belong to that connection family. The connection is
published only after this response is complete and validated. Unknown/rejected settings fail Open.
Result identifiers and error handling keep their existing UTF-8 contract; ordinary result values
continue to use their column collations.

The canonical name partitions pools and survives endpoint and credential snapshots. Following
`COM_RESET_CONNECTION`, managed settings are reapplied and verified before later autocommit or
database initialization. Failed/canceled/timed-out restoration discards the physical session.
Statement-cache IDs remain per physical session and reset generation; no ID crosses charset pools.

## Cached session trust

Initialization establishes a baseline, not continuous observation. Applications must keep client,
connection and result encodings fixed for the connection lifetime. Do not execute independent
`SET NAMES`/charset changes or allow procedures/functions/triggers to change them. Close/Open with
the desired connection option to change encoding.

SQL classified as stateless keeps the cached baseline and introduces no charset query before its
next command. Potentially stateful/opaque SQL invalidates trust when sent; a reported mismatching
charset session variable also invalidates it. The next encoding-bearing request checks the four
settings before transmitting its payload. A mismatch or malformed response breaks the session.
The existing conservative classifier includes CALL/SET/PLSQL, executable comments, user variables
and MySQL quoted backslashes. Therefore some otherwise ordinary SQL also causes rechecking.

**A recheck is an actual SELECT.** After classified SQL it can change session diagnostic functions
such as `ROW_COUNT()`, `FOUND_ROWS()` and warning diagnostics. Use returned ADO.NET counts/results
and avoid depending on those session functions across such commands. Hidden effects of arbitrary
functions/triggers are not comprehensively detected. This is the same incomplete implicit-state
boundary described in the pool isolation contract, not a claim of arbitrary SQL parsing.

Verification never enters a staged stream/piece upload or unread response. Trust is checked before
uploads; a later reported change while data is staged breaks the session before execute. Generated
ASCII XA controls bypass the ordinary query check so they do not gain SQL between XA phases.
Business commands retain their existing XA ownership and active-branch restrictions.

## Command and type boundaries

The selected encoding reaches text/prepare SQL, scalar parameters, ordinary nested object/collection
members and type identities, Oracle PrepareExecute SQL lengths/checksum and array columns, procedure
inputs, metadata lookup parameters, KeyInfo rewrites and INSERT rewrite budgets. Byte limits use
the encoding of the actual payload. Unrepresentable target SQL or ordinary parameters fail local
prevalidation before target preparation/execution.

MySQL string client-prepare fallback retains its trusted UTF-8-only collation/sql_mode rules;
non-UTF8 string fallback is rejected. Use server preparation for those character sets. UTF8MB3
fallback must match the configured input family and rejects supplementary text. Oracle scalar
literal fallback uses the selected encoding for its 4000-byte limit. This module does not extend
DBCS string literal fallback escaping.

Explicit MySQL JSON parameters use the selected strict client encoding and the existing ordinary
string wire type253. Unrepresentable text fails prevalidation before target preparation/execution;
ASCII JSON escapes can represent characters outside the selected charset. The driver preserves
JSON text rather than parsing or serializing it. SQL NULL remains distinct from JSON text `null`.
Oracle mode and nonzero Precision/Scale remain rejected, including for SQL NULL. JSON results
retain strict UTF-8 decoding. Server preparation/fallback restrictions above still apply.

The fixed OceanBase source's
[is_valid_for_json](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/src/sql/engine/expr/ob_expr_json_func_helper.cpp#L1263)
requests UTF8MB4 calculation collation for nonbinary strings from other charsets. This is source
evidence for server-side conversion, not a completed deployment/storage matrix.
Standalone NCHAR/NVARCHAR2
parameters retain the independently verified national encoding. Complex national members already depend on connection charset and
remain limited to the existing UTF8/UTF16 names; non-UTF8 managed names and the explicit utf8mb3
name are rejected by that path. CLOB write buffers use ordinary string parameters and therefore
inherit the selected client encoding. The complete WriteText input is checked for representability
before its first write chunk, using bounded chunks for encoding checks; a rejected suffix leaves
the locator and handle usable. Later
server/network failures can still leave a completed prefix, as documented by the LOB mutation API.
CLOB reads retain the existing
UTF-8 contract. LOB locator bytes, binary streams, CLOB storage rules, LOCAL INFILE filename rules
and remaining extended metadata are not expanded by this option.

## Evidence boundary

Byte-exact loopback fixtures cover all twelve non-default names in both compatibility modes,
sync/async text and server preparation, UTF-8 results/identifiers, EOF variants and pool restoration.
Additional local regressions cover trust invalidation, procedure outputs, staged data, generated XA
controls, nested complex/array encodings, byte budgets and strict alias rejection. Five product assets
are consumed in one .NET10 process; this does not establish five independent runtimes.

The initial client-encoding module used local fixtures only. The JSON extension adds exact bytes
for all twelve non-default names, both row protocols and sync/async, plus five-asset consumption.
Its opt-in MySQL tests check JSON storage, escaped supplementary values, SQL NULL versus JSON null,
invalid input and subsequent command reuse. The current real attempt failed all ten cases while
reading the initial greeting, before authentication or JSON SQL; both existing default-charset
connection controls failed there too and a raw TCP probe received zero greeting bytes. The underlying
server/network cause is unknown. No real JSON storage or OBProxy success is claimed.
Accepted server names, collations, storage/coercibility, national/JSON/CLOB combinations and
version/proxy/reset support still require the corresponding real-server matrix. This delivers
local client-encoding behavior, not all P1-08.
