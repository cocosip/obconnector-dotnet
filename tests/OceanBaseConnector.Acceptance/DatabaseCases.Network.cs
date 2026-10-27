using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static void AddNetwork(List<AcceptanceCase> cases)
    {
        foreach (var asynchronous in new[] { false, true })
            foreach (var operation in new[] { "insert", "commit", "rollback" })
                foreach (var point in new[] { CommandDropPoint.BeforeForward, CommandDropPoint.PartialRequest, CommandDropPoint.WholeResponse, CommandDropPoint.PartialResponse })
                {
                    if (operation != "insert" && point is CommandDropPoint.BeforeForward or CommandDropPoint.PartialRequest) continue;
                    var boundary = point switch
                    {
                        CommandDropPoint.BeforeForward => "before-forward",
                        CommandDropPoint.PartialRequest => "partial-request",
                        CommandDropPoint.WholeResponse => "whole-response",
                        _ => "partial-response",
                    };
                    cases.Add(new("matrix.network-outcome-unknown." + operation + "." + boundary + (asynchronous ? ".async" : ".sync"),
                        "faults", (c, t) => NetworkOutcome(c, asynchronous, operation, point, t),
                        RequiresObjects: true, RequiresNetworkFaultProfile: true));
                }
    }

    private static async Task NetworkOutcome(CaseContext c, bool asynchronous, string operation, CommandDropPoint point, CancellationToken token)
    {
        TransactionResponseDropProxy? proxy = null; OceanBaseDataSource? source = null;
        OceanBaseConnection? observer = null; OceanBaseConnection? connection = null; OceanBaseTransaction? transaction = null;
        Exception? failure = null;
        try
        {
            using var configuration = c.CreateConnection();
            var builder = new OceanBaseConnectionStringBuilder(configuration.ConnectionString) { Pooling = false };
            observer = new OceanBaseConnection(builder.ConnectionString); await observer.OpenAsync(token); c.ObserveServer(observer);
            var table = await c.TableAsync(observer, "id INTEGER PRIMARY KEY", token);
            var insert = "INSERT INTO " + table + " VALUES (1)";
            var target = operation == "insert" ? insert : operation == "commit" ? "COMMIT" : "ROLLBACK";
            proxy = new TransactionResponseDropProxy(builder.Server, builder.Port, target, point);
            builder.Server = "127.0.0.1"; builder.Port = proxy.Port;
            builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 1;
            builder.ConnectionLifetime = 0; builder.ConnectionIdleLifetime = 0;
            source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
            connection = (OceanBaseConnection)source.CreateConnection(); await Open();
            if (operation != "insert")
            {
                transaction = asynchronous ? (OceanBaseTransaction)await connection.BeginTransactionAsync(token) : (OceanBaseTransaction)connection.BeginTransaction();
                await Execute(insert, transaction);
                c.Check(await c.CountAsync(observer, table, token) == 0, "Uncommitted test write must be invisible to the observer.");
            }
            proxy.Arm();
            var unknown = false;
            try
            {
                if (operation == "insert") await Execute(insert);
                else if (asynchronous)
                { if (operation == "commit") await transaction!.CommitAsync(token); else await transaction!.RollbackAsync(token); }
                else { if (operation == "commit") transaction!.Commit(); else transaction!.Rollback(); }
            }
            catch (OceanBaseException error) when (!token.IsCancellationRequested && error.IsExecutionOutcomeUnknown) { unknown = true; }
            c.Check(unknown, "Interrupted network write must report unknown execution outcome.");
            c.Check(connection.State == ConnectionState.Broken, "Interrupted network write must break its connection.");
            c.Check(transaction == null || transaction.Connection == null, "Interrupted transaction end must detach its transaction.");
            c.Check(proxy.TargetRequests == 1, "The target write must be sent exactly once without replay.");
            var responseLost = point is CommandDropPoint.WholeResponse or CommandDropPoint.PartialResponse;
            c.Check(proxy.ObservedResponseMarker == (responseLost ? (byte?)0 : null), "The relay must observe the expected response boundary.");
            var expected = responseLost && operation != "rollback" ? 1 : 0;
            c.Check(await c.CountAsync(observer, table, token) == expected, "Reconciliation must match the injected request/response boundary.");
            await Close(); await Open();
            c.Check(proxy.Connections == 2, "Recovery must replace the broken physical channel in the private capacity-one pool.");
            if (transaction != null) { if (asynchronous) await transaction.DisposeAsync(); else transaction.Dispose(); }
            await Execute("INSERT INTO " + table + " VALUES (2)");
            c.Check(await c.CountAsync(observer, table, token) == expected + 1, "Reconciliation after recovery must contain only the expected fresh write.");
            await using (var fresh = asynchronous ? (OceanBaseTransaction)await connection.BeginTransactionAsync(token) : (OceanBaseTransaction)connection.BeginTransaction())
            {
                await Execute("INSERT INTO " + table + " VALUES (3)", fresh);
                if (asynchronous) await fresh.RollbackAsync(token); else fresh.Rollback();
            }
            c.Check(await c.CountAsync(observer, table, token) == expected + 1, "Fresh rollback must not leak transaction state across recovery.");
            c.Check(proxy.TargetRequests == (operation == "rollback" ? 2 : 1), "Recovery must not replay the interrupted target command.");
            source.Clear(); await Close();
            await proxy.Completion.WaitAsync(token);

            async Task Open() { if (asynchronous) await connection.OpenAsync(token); else connection.Open(); }
            async Task Close() { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }
            async Task Execute(string sql, OceanBaseTransaction? tx = null)
            {
                using var command = c.Command(connection, sql, tx);
                c.Check((asynchronous ? await command.ExecuteNonQueryAsync(token) : command.ExecuteNonQuery()) == 1, "Test write must affect exactly one row.");
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var errors = new List<Exception>();
            foreach (var cleanup in new Func<ValueTask>[] { () => transaction?.DisposeAsync() ?? default, () => connection?.DisposeAsync() ?? default,
                () => source?.DisposeAsync() ?? default, () => proxy?.DisposeAsync() ?? default, () => observer?.DisposeAsync() ?? default })
            { try { await cleanup(); } catch (Exception error) { errors.Add(error); } }
            if (errors.Count != 0) { if (failure != null) errors.Insert(0, failure); throw new AggregateException(errors); }
        }
    }
}
