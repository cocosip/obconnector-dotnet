using System;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class OracleDdlMetadataTests
    {
        [Theory]
        [InlineData("ALWAYS", "start with 17 nocache")]
        [InlineData("BY DEFAULT", "increment by 5 start with 17 cache 4 noorder nocycle")]
        [InlineData("BY DEFAULT ON NULL", "(MINVALUE -20 START WITH -17 INCREMENT BY -1 NOCACHE)")]
        public void ReadsOnlyIdentityColumnAndPreservesOptions(string mode, string options)
        {
            var ddl = "CREATE TABLE \"S\".\"T(\" (" +
                "\"TEXT\" VARCHAR2(100) DEFAULT q'[it’s ' GENERATED ALWAYS AS IDENTITY, )]', " +
                "\"DEFAULT_SEQ\" NUMBER DEFAULT \"S\".\"SEQ\".NEXTVAL, " +
                "\"I\"\"D\" NUMBER(10) GENERATED " + mode + " AS IDENTITY " + options + " NOT NULL ENABLE, " +
                "CONSTRAINT \"PRIMARY\" PRIMARY KEY (\"I\"\"D\"))";
            var column = Assert.Single(OracleDdlMetadata.ReadIdentityColumns(ddl));
            Assert.Equal("I\"D", column.ColumnName);
            Assert.Equal(mode, column.GenerationType);
            Assert.Equal(options, column.SequenceOptions);
        }

        [Fact]
        public void HandlesQuotedKeywordsAndTopLevelConstraintClauses()
        {
            const string ddl = "CREATE TABLE \"T\" (\"GENERATED\" NUMBER DEFAULT 1, " +
                "\"CHECK\" VARCHAR2(50) DEFAULT 'GENERATED ALWAYS AS IDENTITY', " +
                "CONSTRAINT \"C\" CHECK (1 = 1))";
            Assert.Empty(OracleDdlMetadata.ReadIdentityColumns(ddl));
            Assert.Throws<NotSupportedException>(() => OracleDdlMetadata.ReadIdentityColumns("CREATE TABLE \"T\" (ID NUMBER"));
        }

        [Theory]
        [InlineData("-17")]
        [InlineData("99999999999999999999999999999999999999")]
        public void ReadsExactSequenceStartFromOptions(string start)
        {
            Assert.Equal(start, OracleDdlMetadata.ReadSequenceStart(
                "CREATE SEQUENCE \"S\".\"START WITH 2\" minvalue -20 /* START WITH 3 */ start with " + start + " nocache").ToString());
        }

        [Theory]
        [InlineData("CREATE SEQUENCE \"START WITH 3\" INCREMENT BY 1")]
        [InlineData("CREATE SEQUENCE S START WITH 1.5")]
        [InlineData("CREATE SEQUENCE S START WITH 1 START WITH 2")]
        public void RejectsMissingOrAmbiguousSequenceStart(string ddl)
            => Assert.Throws<NotSupportedException>(() => OracleDdlMetadata.ReadSequenceStart(ddl));
    }
}
