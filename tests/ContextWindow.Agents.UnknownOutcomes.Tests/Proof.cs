using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using Xunit.Abstractions;
namespace ContextWindow.Tests;

public sealed class IntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public Task CrashSafeRecovery() => Proof.RunAsync(output.WriteLine);
}
internal static class Proof
{
    private static string address = "";
    private static readonly Ledger Ledger = new();
    internal static async Task RunAsync(Action<string> log)
    {
        var watch = Stopwatch.StartNew();
        await Schema.ResetAsync();
        await Schema.ResetAsync();
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        address = $"http://127.0.0.1:{port}/";
        using var service = Child.Start("service", address);
        await service.UntilAsync("READY");
        log("fixture: PostgreSQL, separate HTTP downstream process, real worker termination barriers, Agent Framework + scripted IChatClient; no model requests");
        await BaselineAsync(log);
        foreach (var mode in Enum.GetValues<Capability>())
            foreach (var fault in new[] { Fault.Prepared, Fault.Dispatching, Fault.ResponseLost, Fault.ReceiptReceived, Fault.Saved })
                await MatrixAsync(mode, fault, log);
        await IdentityAsync(log);
        await CompetingAsync(log);
        await SafetyAsync(log);
        var durable = await NewAsync("downstream-restart");
        await WorkerAsync(durable, Capability.Idempotency, Fault.ResponseLost);
        var original = await OriginalAsync(durable);
        service.Dispose();
        using var restartedService = Child.Start("service", address);
        await restartedService.UntilAsync("READY");
        var recovered = await WorkerAsync(durable, Capability.Idempotency);
        Assert.Equal(original, recovered.Receipt);
        Assert.Equal(1, await CountAsync(durable));
        log($"downstream-restart mutations=1 original={original.Id} recovered={recovered.Receipt!.Id}");
        log($"PASS elapsed_ms={watch.ElapsedMilliseconds} parent_peak_working_set_bytes={Process.GetCurrentProcess().PeakWorkingSet64} largest_child_sampled_working_set_bytes={Child.LargestSampledWorkingSet}; excludes PostgreSQL memory, not a benchmark");
    }
    private static async Task<Write> NewAsync(string intent, string tenant = "north-shop") => new(tenant, await Ledger.BindIntentAsync(tenant, intent), "order-731", 4850, "USD");
    private static Child StartWorker(Write write, Capability mode, Fault fault) => Child.Start("worker", address, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Json.Encode(write))), mode.ToString(), fault.ToString());
    private static async Task<ToolResult> WorkerAsync(Write write, Capability mode, Fault fault = Fault.None)
    {
        using var worker = StartWorker(write, mode, fault);
        var result = Json.Decode<ToolResult>(await worker.UntilAsync("RESULT:"));
        await worker.WaitAsync();
        Assert.Equal(result.Status == "succeeded", result.Receipt is not null);
        if (result.Receipt is not null)
            Assert.Equal(write, result.Receipt.Write);
        else
            Assert.Equal("check_status_do_not_resubmit", result.NextAction);
        return result;
    }
    private static Task<long> CountAsync(Write write) => Database.ScalarAsync<long>("SELECT count(*) FROM unknown_downstream.mutations WHERE tenant=$1 AND operation=$2", write.Tenant, write.OperationId);
    private static Task<long> SendsAsync(Write write) => Database.ScalarAsync<long>("SELECT count(*) FROM unknown_downstream.requests WHERE tenant=$1 AND operation=$2 AND kind='write'", write.Tenant, write.OperationId);
    private static async Task<Receipt> OriginalAsync(Write write) => Json.Decode<Receipt>(await Database.ScalarAsync<string>("SELECT payload FROM unknown_downstream.mutations WHERE tenant=$1 AND operation=$2 LIMIT 1", write.Tenant, write.OperationId));
    private static async Task MatrixAsync(Capability mode, Fault fault, Action<string> log)
    {
        var write = await NewAsync($"matrix-{mode}-{fault}");
        if (fault == Fault.ResponseLost)
        {
            Assert.Equal("pending", (await WorkerAsync(write, mode, fault)).Status);
            Assert.Equal("unknown", (await Ledger.LoadAsync(write)).State);
        }
        else
        {
            using var worker = StartWorker(write, mode, fault);
            await worker.UntilAsync("BARRIER:" + fault);
            var expected = fault switch
            {
                Fault.Prepared => "prepared",
                Fault.Saved => "succeeded",
                _ => "dispatching"
            };
            Assert.Equal(expected, (await Ledger.LoadAsync(write)).State);
        } // Kills the real worker and waits for exit before recovery.
        var before = await CountAsync(write);
        var committed = fault is Fault.ResponseLost or Fault.ReceiptReceived or Fault.Saved;
        Assert.Equal(committed ? 1 : 0, before);
        Receipt? original = committed ? await OriginalAsync(write) : null;
        var recovered = await WorkerAsync(write, mode);
        var shouldSucceed = fault is Fault.Prepared or Fault.Saved || mode == Capability.Idempotency || (mode == Capability.Lookup && committed);
        Assert.Equal(shouldSucceed ? "succeeded" : "pending", recovered.Status);
        Assert.Equal(shouldSucceed ? "succeeded" : "unknown", (await Ledger.LoadAsync(write)).State);
        Assert.Equal(committed || shouldSucceed ? 1 : 0, await CountAsync(write));
        if (shouldSucceed && original is not null)
            Assert.Equal(original, recovered.Receipt);
        var sends = await SendsAsync(write);
        Assert.Equal(fault switch
        {
            Fault.Prepared => 1L,
            Fault.Dispatching => mode == Capability.Idempotency ? 1L : 0L,
            Fault.Saved => 1L,
            _ => mode == Capability.Idempotency ? 2L : 1L
        }, sends);
        var again = await WorkerAsync(write, mode);
        Assert.Equal(recovered, again);
        Assert.Equal(sends, await SendsAsync(write));
        var history = await Database.ScalarAsync<string[]>("SELECT history FROM unknown_ledger.operations WHERE tenant=$1 AND operation=$2", write.Tenant, write.OperationId);
        Assert.Equal("prepared", history[0]);
        Assert.Contains("dispatching", history);
        if (fault is Fault.ResponseLost or Fault.ReceiptReceived or Fault.Dispatching)
            Assert.Contains("unknown", history);
        log($"protected mode={mode} fault={fault} mutations={await CountAsync(write)} sends={sends} state={(await Ledger.LoadAsync(write)).State} receipt={recovered.Receipt?.Id ?? "null"} original={original?.Id ?? "none-before-restart"} history={string.Join("->", history)}");
    }
    private static async Task BaselineAsync(Action<string> log)
    {
        var first = await NewAsync("baseline-original");
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
        var lost = false;
        try
        {
            using var response = await http.PostAsJsonAsync("write?mode=Idempotency&drop=True", first);
            await response.Content.ReadFromJsonAsync<Receipt>();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException) { lost = true; }
        Assert.True(lost);
        var retry = first with
        {
            OperationId = Guid.NewGuid().ToString("N")
        };
        using var second = await http.PostAsJsonAsync("write?mode=Idempotency", retry);
        second.EnsureSuccessStatusCode();
        var receipt = await second.Content.ReadFromJsonAsync<Receipt>();
        Assert.NotNull(receipt);
        Assert.Equal(2, await CountAsync(first) + await CountAsync(retry));
        log($"baseline fault=ResponseLost fresh-key-retry mutations=2 reported=succeeded original={(await OriginalAsync(first)).Id} retry_receipt={receipt.Id}");
    }
    private static async Task IdentityAsync(Action<string> log)
    {
        var write = await NewAsync("identity");
        Assert.Equal(write.OperationId, await Ledger.BindIntentAsync(write.Tenant, "identity"));
        await WorkerAsync(write, Capability.None);
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        var executor = new Executor(Ledger, http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(write with { Cents = 9000 }, Capability.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(write, Capability.Lookup));
        Assert.Equal(1, await SendsAsync(write));
        var intentional = await NewAsync("intentional-equal-arguments");
        Assert.Equal(write.Arguments, intentional.Arguments);
        Assert.NotEqual(write.OperationId, intentional.OperationId);
        await WorkerAsync(intentional, Capability.None);
        Assert.Equal(1, await CountAsync(intentional));
        var otherTenant = write with
        {
            Tenant = "south-shop"
        };
        var result = await WorkerAsync(otherTenant, Capability.None);
        Assert.Equal(otherTenant, result.Receipt!.Write);
        Assert.NotEqual((await OriginalAsync(write)).Id, result.Receipt.Id);
        log("identity: durable intent binding; fresh agent call IDs; changed arguments/capability rejected before send; two intentional equal actions allowed; tenant-scoped receipts isolated");
    }
    private static async Task CompetingAsync(Action<string> log)
    {
        foreach (var mode in Enum.GetValues<Capability>())
        {
            var race = await NewAsync("race-" + mode);
            await Ledger.PrepareAsync(race, mode);
            var results = await Task.WhenAll(WorkerAsync(race, mode), WorkerAsync(race, mode));
            Assert.Equal(1, await CountAsync(race));
            if (mode != Capability.Idempotency)
                Assert.Equal(1, await SendsAsync(race));
            Assert.Contains(results, result => result.Status == "succeeded");
            var paused = await NewAsync("paused-" + mode);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
            var executor = new Executor(Ledger, http, async _ => { reached.SetResult(); await release.Task; });
            var late = executor.ExecuteAsync(paused, mode, Fault.Dispatching);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var recovering = await WorkerAsync(paused, mode);
            Assert.Equal(mode == Capability.Idempotency ? "succeeded" : "pending", recovering.Status);
            release.SetResult();
            var lateResult = await late;
            Assert.Equal("succeeded", lateResult.Status);
            Assert.Equal(1, await CountAsync(paused));
            Assert.Equal(mode == Capability.Idempotency ? 2 : 1, await SendsAsync(paused));
            await Ledger.UnknownAsync(paused);
            Assert.Equal("succeeded", (await Ledger.LoadAsync(paused)).State);
        }
        log("concurrency: competing real workers, paused sender + recovery, late receipt and late timeout: one mutation in every mode");
    }
    private static async Task SafetyAsync(Action<string> log)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
        var executor = new Executor(Ledger, http);
        var expiry = await NewAsync("expired");
        await WorkerAsync(expiry, Capability.Idempotency, Fault.ResponseLost);
        await Database.ExecuteAsync("UPDATE unknown_ledger.operations SET replay_until=now()-interval '1 second' WHERE tenant=$1 AND operation=$2", expiry.Tenant, expiry.OperationId);
        Assert.Equal("pending", (await WorkerAsync(expiry, Capability.Idempotency)).Status);
        Assert.Equal(1, await SendsAsync(expiry));
        var miss = await NewAsync("lookup-miss");
        await WorkerAsync(miss, Capability.Lookup, Fault.ResponseLost);
        Assert.Equal("pending", (await executor.ExecuteAsync(miss, Capability.Lookup, Fault.LookupMiss)).Status);
        Assert.Equal(1, await SendsAsync(miss));
        Assert.Equal(await OriginalAsync(miss), (await WorkerAsync(miss, Capability.Lookup)).Receipt);
        var bad = await NewAsync("bad-receipt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(bad, Capability.Lookup, Fault.BadReceipt));
        Assert.Null((await Ledger.LoadAsync(bad)).Receipt);
        Assert.Equal(await OriginalAsync(bad), (await WorkerAsync(bad, Capability.Lookup)).Receipt);
        var outage = await NewAsync("receipt-store-outage");
        await Database.ExecuteAsync("""
          CREATE FUNCTION unknown_ledger.reject_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN IF NEW.state='succeeded' THEN RAISE EXCEPTION 'injected receipt storage outage'; END IF; RETURN NEW; END $$;
          CREATE TRIGGER receipt_outage BEFORE UPDATE ON unknown_ledger.operations FOR EACH ROW EXECUTE FUNCTION unknown_ledger.reject_receipt();
          """);
        try
        {
            Assert.Equal("pending", (await executor.ExecuteAsync(outage, Capability.Lookup)).Status);
            Assert.Equal(1, await CountAsync(outage));
            Assert.Null((await Ledger.LoadAsync(outage)).Receipt);
        }
        finally { await Database.ExecuteAsync("DROP TRIGGER receipt_outage ON unknown_ledger.operations; DROP FUNCTION unknown_ledger.reject_receipt()"); }
        Assert.Equal(await OriginalAsync(outage), (await WorkerAsync(outage, Capability.Lookup)).Receipt);
        log("safety: expired replay held; lookup miss held despite committed mutation; wrong-operation receipt rejected; receipt-store database failure recovered via original receipt");
    }
}
