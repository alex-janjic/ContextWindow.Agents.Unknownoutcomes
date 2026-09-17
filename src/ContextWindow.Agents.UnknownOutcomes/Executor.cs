using System.Net.Http.Json;
using System.Text.Json;
namespace ContextWindow;
/// <summary>Adapter with no HTTP retry middleware. Unknown is never a fresh dispatch permission.</summary>
public sealed class Executor(Ledger ledger, HttpClient http, Func<Fault, Task>? barrier = null)
{
    /// <summary>Execute or recover a trusted logical action. Safe even if recovery overlaps a paused sender.</summary>
    public async Task<ToolResult> ExecuteAsync(Write write, Capability capability, Fault fault = Fault.None, CancellationToken cancellationToken = default)
    {
        var operation = await ledger.PrepareAsync(write, capability);
        if (operation.State == "succeeded")
            return ToolResult.Success(operation.Receipt ?? throw new InvalidOperationException("Success without receipt"));
        await ReachAsync(Fault.Prepared, fault);
        var claimed = operation.State == "prepared" && await ledger.ClaimAsync(write);
        if (claimed)
            await ReachAsync(Fault.Dispatching, fault);
        else
        {
            // Conservative even for a live/paused sender: never grants another unprotected send.
            await ledger.UnknownAsync(write);
            operation = await ledger.LoadAsync(write);
            if (operation.State == "succeeded")
                return ToolResult.Success(operation.Receipt!);
        }
        try
        {
            Receipt? receipt;
            if (claimed)
                receipt = await SendAsync(write, capability, fault, cancellationToken);
            else if (capability == Capability.Lookup)
            {
                using var response = await http.PostAsJsonAsync($"lookup?miss={fault == Fault.LookupMiss}", write, cancellationToken);
                response.EnsureSuccessStatusCode();
                receipt = await response.Content.ReadFromJsonAsync<Receipt>(cancellationToken);
            }
            else if (capability == Capability.Idempotency && operation.ReplayUntil > DateTime.UtcNow)
                receipt = await SendAsync(write, capability, fault, cancellationToken);
            else
                return ToolResult.Pending(write);
            if (receipt is null)
            {
                await ledger.UnknownAsync(write);
                return ToolResult.Pending(write);
            }
            await ReachAsync(Fault.ReceiptReceived, fault);
            await ledger.SaveAsync(write, receipt);
            await ReachAsync(Fault.Saved, fault);
            return ToolResult.Success((await ledger.LoadAsync(write)).Receipt!);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException or Npgsql.NpgsqlException)
        {
            // If the ledger is unavailable, surface that outage; never return success.
            await ledger.UnknownAsync(write);
            operation = await ledger.LoadAsync(write);
            return operation.State == "succeeded" ? ToolResult.Success(operation.Receipt!) : ToolResult.Pending(write);
        }
    }
    private async Task<Receipt?> SendAsync(Write write, Capability capability, Fault fault, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"write?mode={capability}&drop={fault == Fault.ResponseLost}&bad={fault == Fault.BadReceipt}", write, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Receipt>(cancellationToken);
    }
    private Task ReachAsync(Fault point, Fault fault) => point == fault && barrier is not null ? barrier(point) : Task.CompletedTask;
}
