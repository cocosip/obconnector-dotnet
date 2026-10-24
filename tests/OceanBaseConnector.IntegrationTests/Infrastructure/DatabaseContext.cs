using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

/// <summary>
/// Per-test database sandbox: mode-aware SQL helpers plus LIFO object cleanup.
/// Dispose runs the cleanup stack; a cleanup failure fails the owning test
/// even when its body passed, so no created objects are silently abandoned.
/// </summary>
public sealed class DatabaseContext : IAsyncDisposable
{
    private static readonly Regex NamedParameter = new(":[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private readonly List<(string Kind, string Name, Func<Task> Cleanup)> _owned = new();

    public DatabaseContext(IntegrationProfile profile) => Profile = profile;

    public IntegrationProfile Profile { get; }
    public bool Oracle => Profile.Oracle;
    public string SelectOne => Oracle ? "SELECT 1 FROM DUAL" : "SELECT 1";
    public string ScalarSql(string expression) => "SELECT " + expression + (Oracle ? " FROM DUAL" : "");

    public OceanBaseConnection CreateConnection() => Profile.CreateConnection();

    public async Task<OceanBaseConnection> OpenAsync(CancellationToken token = default)
    {
        var connection = CreateConnection();
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    /// <summary>Rewrites fixed :named fixture fragments to MySQL positional markers. Configured SQL is never rewritten.</summary>
    public string ParameterSql(string sql) => Oracle ? sql : NamedParameter.Replace(sql, "?");

    public OceanBaseCommand Command(OceanBaseConnection connection, string sql, OceanBaseTransaction? transaction = null)
    {
        var builder = Profile.CreateBuilder();
        return new OceanBaseCommand { Connection = connection, CommandText = sql, Transaction = transaction, CommandTimeout = builder.CommandTimeout };
    }

    public async Task<int> ExecuteAsync(OceanBaseConnection connection, string sql, CancellationToken token,
        OceanBaseTransaction? transaction = null)
    {
        await using var command = Command(connection, sql, transaction);
        return await command.ExecuteNonQueryAsync(token);
    }

    public async Task<object?> ScalarAsync(OceanBaseConnection connection, string sql, CancellationToken token,
        OceanBaseTransaction? transaction = null)
    {
        await using var command = Command(connection, sql, transaction);
        return await command.ExecuteScalarAsync(token);
    }

    public async Task<long> CountAsync(OceanBaseConnection connection, string table, CancellationToken token,
        OceanBaseTransaction? transaction = null)
        => IntegerResult(await ScalarAsync(connection, "SELECT COUNT(*) FROM " + table, token, transaction));

    public string NewObject(string kind)
    {
        if (kind is not ("TABLE" or "TYPE" or "PROCEDURE")) throw new ArgumentException("Unsupported test object.");
        var name = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        RegisterCleanup(kind, name, async () =>
        {
            await using var connection = await OpenAsync(CancellationToken.None);
            await ExecuteAsync(connection, "DROP " + kind + " " + name, CancellationToken.None);
        });
        return name;
    }

    public void RegisterCleanup(string kind, string name, Func<Task> cleanup) =>
        _owned.Add((kind, name, cleanup));

    public async Task<string> TableAsync(OceanBaseConnection connection, string columns, CancellationToken token)
    {
        var name = NewObject("TABLE");
        try { await ExecuteAsync(connection, "CREATE TABLE " + name + " (" + columns + ")", token); }
        catch (OceanBaseException error) when (error.ServerErrorCode != 0 && !error.IsExecutionOutcomeUnknown)
        {
            _owned.RemoveAll(item => item.Kind == "TABLE" && item.Name == name);
            throw;
        }
        return name;
    }

    public static OceanBaseParameter Parameter(OceanBaseCommand command, string name, object? value, DbType? type = null)
    {
        var parameter = new OceanBaseParameter { ParameterName = name, Value = value };
        if (type.HasValue) parameter.DbType = type.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    public static long IntegerResult(object? value)
    {
        Assert.False(value is null || value is DBNull, "An integer scalar result is required.");
        var number = value is OceanBaseDecimal exact ? exact.ToDecimal() : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        Assert.True(decimal.Truncate(number) == number, $"An integer scalar result is required, got {number}.");
        return checked((long)number);
    }

    public static void EqualNumber(object? value, long expected, string message) =>
        Assert.True(IntegerResult(value) == expected, message);

    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        foreach (var (kind, name, cleanup) in _owned.AsEnumerable().Reverse())
        {
            try { await cleanup(); }
            catch (Exception error) { errors.Add(new InvalidOperationException($"Cleanup {kind} {name} failed: {error.Message}", error)); }
        }
        if (errors.Count > 0) throw new AggregateException("Database cleanup failed; created objects may remain.", errors);
    }
}
