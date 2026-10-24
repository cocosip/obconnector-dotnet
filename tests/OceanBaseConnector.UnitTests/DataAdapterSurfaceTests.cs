using System;
using System.Data;
using System.Data.Common;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class DataAdapterSurfaceTests
    {
        [Theory]
        [InlineData("Oracle", "\"")]
        [InlineData("MySql", "`")]
        public void BuilderQuotesIdentifiersFromExpectedModeWithoutOpening(string mode, string quote)
        {
            using var connection = new OceanBaseConnection("Server=localhost;User ID=u;ExpectedMode=" + mode);
            using var adapter = new OceanBaseDataAdapter("select id from ledger", connection);
            using var builder = new OceanBaseCommandBuilder(adapter);
            Assert.Equal(quote, builder.QuotePrefix); Assert.Equal(quote, builder.QuoteSuffix);
            var identifier = "a" + quote + "b; DROP TABLE ledger";
            Assert.Equal(quote + identifier.Replace(quote, quote + quote) + quote, builder.QuoteIdentifier(identifier));
            Assert.Equal(identifier, builder.UnquoteIdentifier(builder.QuoteIdentifier(identifier)));
            Assert.Equal("plain", builder.UnquoteIdentifier("plain")); Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Throws<ArgumentNullException>(() => builder.QuoteIdentifier(null!)); Assert.Throws<ArgumentNullException>(() => builder.UnquoteIdentifier(null!));
        }

        [Fact]
        public void BuilderRequiresKnownSyntaxOrExplicitMatchingQuotes()
        {
            using var builder = new OceanBaseCommandBuilder();
            Assert.Throws<InvalidOperationException>(() => builder.QuoteIdentifier("id"));
            Assert.Throws<ArgumentException>(() => builder.QuotePrefix = "[");
            builder.QuotePrefix = "\""; builder.QuoteSuffix = "`";
            Assert.Throws<InvalidOperationException>(() => builder.QuoteIdentifier("id"));
            builder.QuoteSuffix = "\""; Assert.Equal("\"id\"", builder.QuoteIdentifier("id"));
            Assert.Throws<InvalidOperationException>(() => builder.GetInsertCommand());
        }

        [Fact]
        public void AdapterConstructorsAndTypedCommandsKeepCallerObjects()
        {
            using var connection = new OceanBaseConnection(); using var select = new OceanBaseCommand();
            using var adapter = new OceanBaseDataAdapter(select); Assert.Same(select, adapter.SelectCommand);
            using var update = new OceanBaseCommand(); adapter.InsertCommand = update; adapter.UpdateCommand = update; adapter.DeleteCommand = update;
            Assert.Same(update, ((DbDataAdapter)adapter).InsertCommand); Assert.Same(update, adapter.UpdateCommand); Assert.Same(update, adapter.DeleteCommand);
            Assert.Equal(1, adapter.UpdateBatchSize); Assert.Throws<NotSupportedException>(() => adapter.UpdateBatchSize = 0); Assert.Throws<NotSupportedException>(() => adapter.UpdateBatchSize = 2);
            using var text = new OceanBaseDataAdapter("select 1", connection); Assert.Same(connection, text.SelectCommand!.Connection); Assert.Equal("select 1", text.SelectCommand.CommandText);
            using var owned = new OceanBaseDataAdapter("select 2", "Server=localhost;User ID=u"); Assert.Equal(ConnectionState.Closed, owned.SelectCommand!.Connection!.State);
        }

        [Fact]
        public void BuilderDetachesWhenReassignedOrDisposed()
        {
            using var one = new OceanBaseDataAdapter(); using var two = new OceanBaseDataAdapter();
            using var builder = new OceanBaseCommandBuilder(one); builder.DataAdapter = two;
            using var first = new DataTable(); first.Columns.Add("x", typeof(int)); first.Rows.Add(1);
            var before = 0; one.RowUpdating += (_, _) => before++;
            Assert.Throws<InvalidOperationException>(() => one.Update(first)); Assert.Equal(1, before);
            builder.Dispose(); Assert.Null(builder.DataAdapter);
            using var second = first.Clone(); second.Rows.Add(2);
            var after = 0; two.RowUpdating += (_, _) => after++;
            Assert.Throws<InvalidOperationException>(() => two.Update(second)); Assert.Equal(1, after);
        }
    }
}
