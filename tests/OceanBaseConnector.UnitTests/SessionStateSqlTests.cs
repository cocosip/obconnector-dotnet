using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class SessionStateSqlTests
    {
        [Theory]
        [InlineData("/* trace */ SeT/* gap */x = 1")]
        [InlineData("-- trace\r\nALTER\tSESSION SET x = 1")]
        [InlineData("CREATE /* gap */ GLOBAL\nTEMPORARY TABLE t (id INT)")]
        [InlineData("DECLARE\tGLOBAL /* gap */ TEMPORARY TABLE t (id INT)")]
        [InlineData("SELECT '; SET x = 0'; /* next */ SET x = 1")]
        [InlineData("SELECT 1; SELECT 2; /* next */ CREATE TEMPORARY TABLE t (id INT)")]
        [InlineData("; ; /* trace */ CALL p()")]
        [InlineData("EXECUTE IMMEDIATE 'SET x = 1'")]
        [InlineData("BEGIN p(); END;")]
        [InlineData("PREPARE p FROM 'SET x = 1'")]
        [InlineData("USE another_database")]
        [InlineData("LOCK TABLES t READ")]
        [InlineData("START TRANSACTION")]
        [InlineData("RESET ROLE")]
        [InlineData("XA START 'branch'")]
        public void StatefulStatementsAreRecognizedInBothModes(string sql)
        {
            Assert.True(SessionStateSql.RequiresDiscard(sql, OceanBaseCompatibilityMode.Oracle));
            Assert.True(SessionStateSql.RequiresDiscard(sql, OceanBaseCompatibilityMode.MySql));
        }

        [Theory]
        [InlineData("SELECT 'SET x = 1; CALL p()'")]
        [InlineData("SELECT 'it''s; SET x = 1'")]
        [InlineData("SELECT \"SET; CALL\" FROM t")]
        [InlineData("SELECT 1 /* ; SET x = 1 */; -- CALL p()\n SELECT 2")]
        [InlineData("SELECT 1; /* SET x = 1 */")]
        [InlineData("UPDATE t SET value = 'CREATE TEMPORARY TABLE t'")]
        [InlineData("INSERT INTO t VALUES ('CALL p()')")]
        [InlineData("DELETE FROM t WHERE id = 1")]
        [InlineData("ALTER TABLE t ADD value INT")]
        [InlineData("SELECT 1 AS setting, 2 AS call_count")]
        [InlineData("SELECT get_lock FROM t")]
        [InlineData("SELECT 1 AS release_lock")]
        [InlineData("SELECT value FROM release_all_locks")]
        [InlineData("INSERT INTO get_lock (id) VALUES (1)")]
        [InlineData("INSERT INTO tenant /* gap */ . get_lock (id) VALUES (1)")]
        [InlineData("CREATE TABLE IF NOT EXISTS release_lock (id INT)")]
        [InlineData("WITH a AS (SELECT 1), get_lock(id) AS (SELECT 2) SELECT * FROM get_lock")]
        [InlineData("SELECT `get_lock` FROM t")]
        [InlineData("SELECT 1 --")]
        [InlineData("/* SET x = 1 */ SELECT 1")]
        public void OrdinarySqlAndQuotedOrCommentedKeywordsRemainReusable(string sql)
        {
            Assert.False(SessionStateSql.RequiresDiscard(sql, OceanBaseCompatibilityMode.Oracle));
            Assert.False(SessionStateSql.RequiresDiscard(sql, OceanBaseCompatibilityMode.MySql));
        }

        [Theory]
        [InlineData("SELECT q'[it's; SET x = 1]' FROM dual", false)]
        [InlineData("SELECT NQ'!it's; CALL p()!' FROM dual", false)]
        [InlineData("SELECT q'{one; 'two'; SET x = 1}' FROM dual; ALTER\nSESSION SET x = 2", true)]
        [InlineData("<<outer_block>> BEGIN p(); END;", true)]
        [InlineData("/* trace */ DECLARE n NUMBER; BEGIN p(); END;", true)]
        [InlineData("/* ! this is ordinary */ SELECT 1", false)]
        [InlineData("SELECT 'a\\b' FROM dual", false)]
        [InlineData("SELECT 1--comment; SET x = 1\nFROM dual", false)]
        public void OracleQuotesLabelsAndCommentsHaveModeSpecificSemantics(string sql, bool expected)
            => Assert.Equal(expected, SessionStateSql.RequiresDiscard(sql, OceanBaseCompatibilityMode.Oracle));

        [Theory]
        [InlineData("# trace\nSET x = 1", true)]
        [InlineData("SELECT 1; # trace\nSET x = 1", true)]
        [InlineData("/*!50000 SET x = 1 */", true)]
        [InlineData("SELECT 1 /*! + 1 */", true)]
        [InlineData("/*M! SET x = 1 */", true)]
        [InlineData("SELECT '/*! SET x = 1 */'", false)]
        [InlineData("SELECT `SET; CALL` FROM t", false)]
        [InlineData("SELECT 'a\\b'", true)]
        [InlineData("SELECT 'a\\'; SET x = 1; -- '", true)]
        [InlineData("SELECT @state := 1", true)]
        [InlineData("SELECT value INTO @state FROM t", true)]
        [InlineData("SELECT @@session.autocommit", false)]
        [InlineData("SELECT GET_LOCK('name', 0)", true)]
        [InlineData("SELECT GET_LOCK /* gap */ ('name', 0)", true)]
        [InlineData("SELECT RELEASE_LOCK -- gap\n ('name')", true)]
        [InlineData("SELECT `GET_LOCK` /* gap */ ('name', 0)", true)]
        [InlineData("SELECT \"RELEASE_LOCK\"('name')", true)]
        [InlineData("SELECT TRIM(BOTH 'x' FROM GET_LOCK('state', 0))", true)]
        [InlineData("SELECT SUBSTRING('abc' FROM \"GET_LOCK\"('state', 0))", true)]
        [InlineData("INSERT INTO get_lock (id) VALUES (GET_LOCK('name', 0))", true)]
        [InlineData("WITH get_lock(id) AS (SELECT GET_LOCK('name', 0)) SELECT * FROM get_lock", true)]
        [InlineData("SELECT RELEASE_ALL_LOCKS()", true)]
        [InlineData("SELECT 1--not_a_comment; SET x = 1", true)]
        [InlineData("SELECT 1-- comment; SET x = 1", false)]
        [InlineData("SELECT 1 --\t SET x = 1", false)]
        [InlineData("SELECT 'GET_LOCK; @state'", false)]
        public void MySqlOpaqueOrAmbiguousStateIsConservativelyRetired(string sql, bool expected)
            => Assert.Equal(expected, SessionStateSql.RequiresDiscard(sql, OceanBaseCompatibilityMode.MySql));
    }
}
