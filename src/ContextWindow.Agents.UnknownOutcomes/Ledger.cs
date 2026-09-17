namespace ContextWindow;
/// <summary>Durable local operation boundary. No transaction includes the downstream service.</summary>
public sealed class Ledger
{
    /// <summary>Bind a trusted tenant-scoped intent to one durable ID, independently of its arguments.</summary>
    public async Task<string> BindIntentAsync(string tenant, string intent)
    {
        await Database.ExecuteAsync("INSERT INTO unknown_ledger.intents VALUES ($1,$2,$3) ON CONFLICT DO NOTHING", tenant, intent, Guid.NewGuid().ToString("N"));
        return await Database.ScalarAsync<string>("SELECT operation FROM unknown_ledger.intents WHERE tenant=$1 AND intent=$2", tenant, intent);
    }
    internal async Task<Operation> PrepareAsync(Write write, Capability capability)
    {
        await Database.ExecuteAsync("INSERT INTO unknown_ledger.operations(tenant,operation,args,payload,capability) VALUES ($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING", write.Tenant, write.OperationId, write.Arguments, Json.Encode(write), capability.ToString());
        var operation = await LoadAsync(write);
        if (operation.Write.Arguments != write.Arguments || operation.Capability != capability)
            throw new InvalidOperationException("Operation identity reused with changed arguments or capability.");
        return operation;
    }
    internal async Task<Operation> LoadAsync(Write write)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = Database.Command(connection, "SELECT payload,capability,state,receipt,replay_until FROM unknown_ledger.operations WHERE tenant=$1 AND operation=$2", write.Tenant, write.OperationId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Operation missing");
        return new(Json.Decode<Write>(reader.GetString(0)), Enum.Parse<Capability>(reader.GetString(1)), reader.GetString(2), reader.IsDBNull(3) ? null : Json.Decode<Receipt>(reader.GetString(3)), reader.GetDateTime(4));
    }
    internal async Task<bool> ClaimAsync(Write write) => await Database.ExecuteAsync(
        "UPDATE unknown_ledger.operations SET state='dispatching',owner=$3,version=version+1,history=history || ARRAY['dispatching'] WHERE tenant=$1 AND operation=$2 AND state='prepared'",
        write.Tenant,
        write.OperationId,
        Guid.NewGuid().ToString("N")) == 1;
    internal Task<int> UnknownAsync(Write write) => Database.ExecuteAsync("UPDATE unknown_ledger.operations SET state='unknown',version=version+1,history=history || ARRAY['unknown'] WHERE tenant=$1 AND operation=$2 AND state='dispatching'", write.Tenant, write.OperationId);
    internal async Task SaveAsync(Write write, Receipt receipt)
    {
        if (receipt.Write != write || string.IsNullOrWhiteSpace(receipt.Id))
            throw new InvalidOperationException("Receipt does not match the operation.");
        await Database.ExecuteAsync("UPDATE unknown_ledger.operations SET state='succeeded',receipt=$3,version=version+1,history=history || ARRAY['succeeded'] WHERE tenant=$1 AND operation=$2 AND state IN ('dispatching','unknown')", write.Tenant, write.OperationId, Json.Encode(receipt));
        var stored = await LoadAsync(write);
        if (stored.Receipt != receipt)
            throw new InvalidOperationException("Conflicting or unsaved receipt.");
    }
}
