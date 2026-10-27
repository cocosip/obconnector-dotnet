using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal sealed class CaseContext(AcceptanceConfig config, string connectionString, List<CreatedObject> objects, List<ServerObservation>? servers = null)
{
    private readonly List<(CreatedObject Item, Func<Task> Cleanup)> _owned = new();
    public AcceptanceConfig Config { get; } = config;
    public bool Oracle => Config.Mode == "Oracle";
    public string SelectOne => Oracle ? "SELECT 1 FROM DUAL" : "SELECT 1";
    public string ScalarSql(string expression) => "SELECT " + expression + (Oracle ? " FROM DUAL" : "");
    public OceanBaseConnection CreateConnection() => new(connectionString);
    public async Task<OceanBaseConnection> OpenAsync(CancellationToken token)
    {
        var connection = CreateConnection();
        try { await connection.OpenAsync(token); ObserveServer(connection); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public void ObserveServer(OceanBaseConnection connection)
    {
        var observation = new ServerObservation(connection.CompatibilityMode?.ToString() ?? "unknown", connection.ServerVersion);
        if (servers != null && !servers.Contains(observation)) servers.Add(observation);
    }
    // Only fixed fixture fragments enter here; configured SQL is never rewritten.
    internal string ParameterSql(string sql) => Oracle ? sql : System.Text.RegularExpressions.Regex.Replace(sql, ":[A-Za-z_][A-Za-z0-9_]*", "?");
    public OceanBaseCommand Command(OceanBaseConnection connection, string sql, OceanBaseTransaction? transaction = null)
        => new() { Connection = connection, CommandText = sql, Transaction = transaction,
            CommandTimeout = new OceanBaseConnectionStringBuilder(connectionString).CommandTimeout };
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
        var name = NewObjectName(kind);
        RegisterCleanup(kind, name, async () =>
        {
            await using var connection = await OpenAsync(CancellationToken.None);
            await ExecuteAsync(connection, "DROP " + kind + " " + name, CancellationToken.None);
        });
        return name;
    }
    public void RegisterCleanup(string kind, string name, Func<Task> cleanup)
    {
        var item = new CreatedObject(kind, name);
        _owned.Add((item, cleanup)); objects.Add(item);
    }

    public async Task<string> TableAsync(OceanBaseConnection connection, string columns, CancellationToken token)
    {
        var name = NewObjectName("TABLE");
        await ExecuteAsync(connection, "CREATE TABLE " + name + " (" + columns + ")", token);
        RegisterCleanup("TABLE", name, async () =>
        {
            await using var cleanup = await OpenAsync(CancellationToken.None);
            await ExecuteAsync(cleanup, "DROP TABLE " + name, CancellationToken.None);
        });
        return name;
    }
    private static string NewObjectName(string kind)
    {
        if (kind != "TABLE" && kind != "TYPE" && kind != "PROCEDURE") throw new ArgumentException("Unsupported test object.");
        return "OBA_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
    }
    internal static long IntegerResult(object? value)
    {
        if (value == null || value is DBNull) throw new AssertionFailure("An integer scalar result is required.");
        var number = value is OceanBaseDecimal exact ? exact.ToDecimal() : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (decimal.Truncate(number) != number) throw new AssertionFailure("An integer scalar result is required.");
        return checked((long)number);
    }
    public void Check(bool condition, string assertion) { if (!condition) throw new AssertionFailure(assertion); }
    public void EqualNumber(object? value, long expected, string assertion)
        => Check(IntegerResult(value) == expected, assertion);
    public async Task<List<string>> CleanupAsync()
    {
        var errors = new List<string>();
        foreach (var owned in _owned.AsEnumerable().Reverse())
        {
            var item = owned.Item;
            try
            {
                await owned.Cleanup();
                item.Cleanup = "removed";
            }
            catch (Exception error)
            {
                item.Cleanup = "failed";
                errors.Add("Cleanup " + item.Kind + " " + item.Name + ": " + AcceptanceSuite.Describe(error));
            }
        }
        return errors;
    }
    public static OceanBaseParameter Parameter(OceanBaseCommand command, string name, object? value, DbType? type = null)
    {
        var parameter = new OceanBaseParameter { ParameterName = name, Value = value };
        if (type.HasValue) parameter.DbType = type.Value;
        command.Parameters.Add(parameter); return parameter;
    }
}
