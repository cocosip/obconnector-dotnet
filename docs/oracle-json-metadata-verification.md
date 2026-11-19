# Oracle JSON and metadata verification

The November 19, 2026 review used OceanBase 4.4.2.2 Oracle and MySQL tenants over direct connections with TLS disabled. The release baseline was OceanBaseConnector NuGet 0.3.1. The regression tests used the source in this repository on .NET 10.

## Verified results

| Area | Observed behavior |
| --- | --- |
| Oracle JSON | NuGet 0.3.1 accepts a string bind as `DbType.String` and throws for an explicit `OceanBaseDbType.Json` bind and native column reads. The source accepts both declarations and returns native JSON as CLR `string` through `GetValue`, `GetString`, and `GetFieldValue<string>`. A SQL NULL returns `DBNull.Value`, while JSON null returns the string `null`. Both text and prepared result rows passed, including a document of more than 5,000 characters with Unicode. Connector/J 2.4.19 also passed with `setString`, `getString`, and `getObject`. |
| Oracle index members | `GetSchema("IndexColumns")` exposes expression SQL in `COLUMN_EXPRESSION` and sorts members by position. Ordinary columns have SQL NULL in the expression field. A valid expression over the catalog's `VARCHAR2(1000)` limit caused error 600 and failed the new regression before the fix. The driver now uses catalog positions and `DBMS_METADATA.GET_DDL('INDEX', ...)` in that case. The regression rebuilt an index using the recovered SQL. The fallback requires access to the index DDL. |
| Oracle Identity | Inserts generated keys in all three modes. `Columns.IS_IDENTITY` reflects `ALL_TAB_COLUMNS.IDENTITY_COLUMN`. SQL NULL means the catalog supplied no answer. Identity catalog queries returned 942 on both tested Oracle accounts. `GetOracleIdentityColumns` and its async counterpart read `ALWAYS`, `BY DEFAULT`, `BY DEFAULT ON NULL`, and the sequence option text from the table DDL. |
| Oracle sequence start | After `NEXTVAL` returned 17 and 22, the start API still read 17 from the sequence DDL. For increment 5, the catalog returned `LAST_NUMBER = 27` with `NOCACHE` and 37 with `CACHE 4`. The latter is the allocation boundary of the cache. |
| Oracle foreign key members | NuGet 0.3.1 and the source before the fix both returned `600/HY000` after about ten seconds from `GetSchema("ForeignKeyColumns")`. Running the original SQL directly reproduced the error with and without the table restriction. `EXPLAIN` also failed on the original SQL and succeeded with explicit join order and hash joins. The hinted query returned the expected row in about 0.9 seconds in the diagnostic probe. Real database tests verify unfiltered and restricted reads and the ordinal pairing of composite foreign keys. |
| MySQL sequence defaults | MariaDB Connector/C 3.4.9 reproduced server errors 1064 for `DEFAULT seq.NEXTVAL` and 1235 for `DEFAULT (seq.NEXTVAL)`. The same server accepted an explicit `seq.NEXTVAL` in `INSERT`. Two triggers on separate tables allocated from one sequence when INSERT omitted the primary key. The same connection read keys 22 and 27 with `CURRVAL`. `LAST_INSERT_ID()` returned 0 and MySQL `INSERT ... RETURNING` returned 1064. Oracle accepted the sequence default and `RETURNING ... INTO`. |

The API calls and minimal SQL examples are in [Schema](schema.md) and [Compatibility and Known Limitations](compatibility.md).

| Test run | Result |
| --- | --- |
| Oracle and MySQL direct full schema suites plus Oracle JSON, Oracle PrepareExecute and LOB locators enabled | 18 passed |
| Oracle direct full schema suite plus JSON with both options disabled | 12 passed |
| MySQL JSON character set and multi-statement regression | 62 passed |
| Release build of all five Connector targets | Passed with zero warnings and errors |
| Full offline solution regression | 10,288 unit, 4,096 consumer, 14 dependency-injection, and 43 integration configuration cases passed |

## Resume the database regression

Use the direct test profiles and a disposable schema in each tenant. The profiles need `createObjects: true`. Configure connection strings through .NET User Secrets on the new machine and refer to the keys from the profile file. The example provides `mysql-direct` and `oracle-direct`. Keep those two entries in a local configuration and set `OB_INTEGRATION_CONFIG` if it is stored outside the integration test project. [Integration configuration](../tests/OceanBaseConnector.IntegrationTests/README.md#configuration-file) · [Example profiles](../tests/OceanBaseConnector.IntegrationTests/integration-tests.example.json)

```powershell
$env:OB_REQUIRE_INTEGRATION = '1'
$env:OB_INTEGRATION_PROFILES = 'oracle-direct,mysql-direct'
$project = 'tests/OceanBaseConnector.IntegrationTests'
$schemaAndOracleJson = 'FullyQualifiedName~IntegrationTests.Schema.SchemaTests|FullyQualifiedName~OracleJsonTests'
$mysqlJson = 'FullyQualifiedName~JsonClientStringsConvertToJsonStorageAndKeepSqlNullDistinct|FullyQualifiedName~MySqlJsonMultiStatement'

dotnet test $project -c Release -m:1 --filter $schemaAndOracleJson

$env:OB_INTEGRATION_PROFILES = 'mysql-direct'
dotnet test $project -c Release -m:1 --filter $mysqlJson
```

For the Oracle comparison, use a second local copy of the configuration with `useOraclePrepareExecute` and `useLobLocators` both set to `false`. Point `OB_INTEGRATION_CONFIG` to that copy, set `OB_INTEGRATION_PROFILES` to `oracle-direct`, and run `dotnet test $project -c Release -m:1 --filter $schemaAndOracleJson`. All three validation runs completed with zero skips.
