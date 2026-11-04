using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexIntervalTextTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var profile in ProfileTheoryData.OracleWithCreateObjects())
            foreach (var precision in new[] { (Leading: 9, Scale: 9), (Leading: 2, Scale: 3), (Leading: 2, Scale: 0) })
                foreach (var nested in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var sequential in new[] { false, true })
                            yield return new object[] { profile, precision.Leading, precision.Scale, nested, asynchronous, sequential };
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task StoredIntervalObjectsKeepExactTextValuesAndIndependentBinaryControl(string profileName, int leading, int scale, bool nested, bool asynchronous, bool sequential)
    {
        var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Exception? failure = null;
        try
        {
            await using var connection = await db.OpenAsync();
            var childName = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, $"CREATE TYPE {childName} AS OBJECT(ym INTERVAL YEAR({leading}) TO MONTH,ds INTERVAL DAY({leading}) TO SECOND({scale}),n NUMBER,s VARCHAR2(40))", CancellationToken.None);
            var rootName = childName;
            if (nested)
            {
                rootName = db.NewObject("TYPE");
                await db.ExecuteAsync(connection, $"CREATE TYPE {rootName} AS OBJECT(child {childName},marker NUMBER)", CancellationToken.None);
            }
            var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,v " + rootName, CancellationToken.None);
            var years = new[]
            {
                new OceanBaseIntervalYearToMonth(true, leading == 9 ? 123456789 : 12, 11, (byte)leading),
                new OceanBaseIntervalYearToMonth(false, 0, 0, (byte)leading),
            };
            var days = new[]
            {
                new OceanBaseIntervalDayToSecond(false, leading == 9 ? 987654321 : 98, 23, 59, 58,
                    scale == 9 ? 123456789 : scale == 3 ? 123000000 : 0, (byte)leading, (byte)scale),
                new OceanBaseIntervalDayToSecond(true, 0, 0, 0, 1,
                    scale == 9 ? 1 : scale == 3 ? 1000000 : 0, (byte)leading, (byte)scale),
            };
            for (var i = 0; i < 3; i++)
            {
                var ym = i == 2 ? "NULL" : $"INTERVAL '{years[i]}' YEAR({leading}) TO MONTH";
                var ds = i == 2 ? "NULL" : $"INTERVAL '{days[i]}' DAY({leading}) TO SECOND({scale})";
                var value = $"{childName}({ym},{ds},17,'sentinel')";
                if (nested) value = $"{rootName}({value},19)";
                await db.ExecuteAsync(connection, $"INSERT INTO {table} VALUES({i + 1},{value})", CancellationToken.None);
            }
            var owner = (string)(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None))!;
            var child = await connection.GetComplexTypeAsync(owner, childName);
            var root = nested ? await connection.GetComplexTypeAsync(owner, rootName) : child;
            var sql = "SELECT v,29 FROM " + table + " ORDER BY id";

            // A separate connection and explicit Prepare verify stored values through the existing binary codec.
            await using (var observer = await db.OpenAsync())
            {
                var observedChild = await observer.GetComplexTypeAsync(owner, childName);
                var observedRoot = nested ? await observer.GetComplexTypeAsync(owner, rootName) : observedChild;
                await using var control = db.Command(observer, sql);
                await control.PrepareAsync();
                await using var reader = await control.ExecuteReaderAsync();
                await Verify(reader, observedRoot, observedChild);
            }
            await using (var query = db.Command(connection, sql))
            {
                var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
                await using var reader = asynchronous ? await query.ExecuteReaderAsync(behavior) : query.ExecuteReader(behavior);
                await Verify(reader, root, child);
            }
            DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1 FROM DUAL", CancellationToken.None), 1, "Text reader must leave its session reusable.");

            async Task Verify(DbDataReader reader, OceanBaseComplexType expectedRoot, OceanBaseComplexType expectedChild)
            {
                Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(0));
                for (var i = 0; i < 3; i++)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    var value = reader.GetFieldValue<OceanBaseComplexValue>(0);
                    Assert.Same(expectedRoot, value.Type);
                    if (nested)
                    {
                        DatabaseContext.EqualNumber(value.GetValues()[1], 19, "Nested marker differs.");
                        value = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[0]);
                    }
                    Assert.Same(expectedChild, value.Type);
                    var members = value.GetValues();
                    if (i == 2) { Assert.Null(members[0]); Assert.Null(members[1]); }
                    else { Assert.Equal(years[i], members[0]); Assert.Equal(days[i], members[1]); }
                    DatabaseContext.EqualNumber(members[2], 17, "Member after INTERVAL differs.");
                    Assert.Equal("sentinel", members[3]);
                    DatabaseContext.EqualNumber(reader.GetValue(1), 29, "Field after OBJECT differs.");
                }
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await db.DisposeAsync(); }
            catch (Exception cleanup) when (failure != null) { throw new AggregateException("Interval text test and cleanup failed.", failure, cleanup); }
        }
    }
}
