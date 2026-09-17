using System.Text.Json;
namespace ContextWindow;

/// <summary>Trusted workflow input. Identity is not a model tool-call ID or argument hash.</summary>
public sealed record Write(string Tenant, string OperationId, string Order, long Cents, string Currency)
{
    /// <summary>Canonical exact business arguments, including the tool name.</summary>
    public string Arguments => JsonSerializer.Serialize(new { Tool = "refund", Order, Cents, Currency });
}
/// <summary>The fixture's explicit downstream contracts.</summary>
public enum Capability
{
    /// <summary>No safe replay or authoritative lookup.</summary>
    None,
    /// <summary>Authoritative receipt lookup without write deduplication.</summary>
    Lookup,
    /// <summary>Durable same-key deduplication with receipt replay.</summary>
    Idempotency
}
/// <summary>Deterministic process barriers and downstream failpoints.</summary>
public enum Fault
{
    /// <summary>No injected fault.</summary>
    None,
    /// <summary>Pause after preparing, before claiming dispatch.</summary>
    Prepared,
    /// <summary>Pause after dispatch authorization, before HTTP.</summary>
    Dispatching,
    /// <summary>Commit downstream and truncate its response.</summary>
    ResponseLost,
    /// <summary>Pause after receiving the receipt, before saving it.</summary>
    ReceiptReceived,
    /// <summary>Pause after saving success and receipt atomically.</summary>
    Saved,
    /// <summary>Return a receipt with the wrong operation identity.</summary>
    BadReceipt,
    /// <summary>Return an inconclusive lookup miss.</summary>
    LookupMiss
}
/// <summary>A durable downstream confirmation bound to exact business details.</summary>
public sealed record Receipt(string Id, Write Write);
/// <summary>The application renders this result, not an unverified model completion claim.</summary>
public sealed record ToolResult(string OperationId, string Status, Receipt? Receipt, string NextAction)
{
    internal static ToolResult Pending(Write write) => new(write.OperationId, "pending", null, "check_status_do_not_resubmit");
    internal static ToolResult Success(Receipt receipt) => new(receipt.Write.OperationId, "succeeded", receipt, "none");
}
internal sealed record Operation(Write Write, Capability Capability, string State, Receipt? Receipt, DateTime ReplayUntil);
internal static class Json
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new JsonException("Missing payload");
}
