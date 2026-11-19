using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.MySql;

public sealed class WideDecimalExpressionTests
{
    public static IEnumerable<object[]> Rows() =>
        from profile in IntegrationEnvironment.Profiles
        where !profile.Oracle && profile.CreateObjects
        from prepared in new[] { false, true }
        from behavior in new[] { CommandBehavior.Default, CommandBehavior.SequentialAccess }
        select new object[] { profile.Name, prepared, behavior };

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task WideDecimalExpressionsReadExactly(string profile, bool prepared, CommandBehavior behavior)
    {
        const string huge = "1234567890123456789012345678901234567.1234567890123456789012345678";
        var hugeCoefficient = BigInteger.Parse(huge.Replace(".", ""), CultureInfo.InvariantCulture);
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profile));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var connection = await db.OpenAsync(token);
        var table = await db.TableAsync(connection, "id INT PRIMARY KEY, n DECIMAL(65,28), huge DECIMAL(65,28)", token);
        await db.ExecuteAsync(connection, $"INSERT INTO {table} VALUES (1,1.25,{huge}), (2,2.50,NULL), (3,NULL,NULL)", token);

        await Check("SUM(n)", "", 81, 28, 375, 2, 3.75m);
        await Check("AVG(n)", "", 69, 30, 1875, 3, 1.875m);
        await Check("COALESCE(SUM(n), 0.0)", "WHERE id = 0", 81, 28, 0, 0, 0m);
        await Check("COALESCE(SUM(n), 0.0)", "WHERE id = 3", 81, 28, 0, 0, 0m);
        await Check("AVG(n)", "WHERE id = 0", 69, 30, null, 0, null);
        await Check("SUM(n)", "WHERE id = 3", 81, 28, null, 0, null);
        await Check("LEAST(n, CAST(2.00 AS DECIMAL(65,28)))", "WHERE id = 2", 81, 28, 2, 0, 2m);
        await Check("GREATEST(n, CAST(2.00 AS DECIMAL(65,28)))", "WHERE id = 2", 81, 28, 25, 1, 2.5m);
        await Check("SUM(huge)", "", 81, 28, hugeCoefficient, 28, null);
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1", token), 1, "Session remains usable.");

        async Task Check(string expression, string where, int precision, int scale, BigInteger? coefficient, int valueScale, decimal? clr)
        {
            await using var command = db.Command(connection, $"SELECT {expression} FROM {table} {where}");
            if (prepared) await command.PrepareAsync(token);
            await using var reader = await command.ExecuteReaderAsync(behavior, token);
            Assert.Equal("DECIMAL", reader.GetDataTypeName(0));
            Assert.Equal(typeof(OceanBaseDecimal), reader.GetFieldType(0));
            Assert.Equal(typeof(OceanBaseDecimal), reader.GetProviderSpecificFieldType(0));
            var column = Assert.Single(reader.GetColumnSchema());
            Assert.Equal(precision, column.NumericPrecision);
            Assert.Equal(scale, column.NumericScale);
            Assert.True(await reader.ReadAsync(token));
            if (coefficient == null)
            {
                Assert.True(await reader.IsDBNullAsync(0, token));
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(DBNull.Value, reader.GetValue(0));
                Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
            }
            else
            {
                var expected = new OceanBaseDecimal(coefficient.Value * BigInteger.Pow(10, scale - valueScale), scale);
                Assert.Equal(expected, await reader.GetFieldValueAsync<OceanBaseDecimal>(0, token));
                Assert.False(await reader.IsDBNullAsync(0, token));
                Assert.False(reader.IsDBNull(0));
                Assert.Equal(expected, reader.GetValue(0));
                Assert.Equal(expected, reader.GetProviderSpecificValue(0));
                Assert.Equal(expected, reader.GetFieldValue<OceanBaseDecimal>(0));
                if (clr.HasValue)
                {
                    Assert.Equal(clr.Value, reader.GetDecimal(0));
                    Assert.Equal(clr.Value, reader.GetFieldValue<decimal>(0));
                    Assert.Equal(clr.Value, await reader.GetFieldValueAsync<decimal>(0, token));
                }
                else
                {
                    Assert.Throws<OverflowException>(() => reader.GetDecimal(0));
                    Assert.Throws<OverflowException>(() => reader.GetFieldValue<decimal>(0));
                    await Assert.ThrowsAsync<OverflowException>(() => reader.GetFieldValueAsync<decimal>(0, token));
                }
            }
            Assert.False(await reader.ReadAsync(token));
        }
    }
}
