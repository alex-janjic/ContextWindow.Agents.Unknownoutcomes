using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
namespace ContextWindow;
/// <summary>Agent Framework tool binding: trusted workflow identity stays outside model arguments.</summary>
public static class RefundTool
{
    /// <summary>Run a turn; the UI consumes only the executor's receipt-gated result.</summary>
    public static async Task<ToolResult> RunAsync(IChatClient client, Executor executor, Write approvedWrite, Capability capability, Fault fault = Fault.None)
    {
        ToolResult? result = null;
        var tool = AIFunctionFactory.Create(async () =>
        {
            result = await executor.ExecuteAsync(approvedWrite, capability, fault);
            return result;
        }, "refund", "Submit or check the already authorized refund. Pending is not completion. Never create a replacement action.");
        var agent = new ChatClientAgent(client, instructions: "Use the refund tool. Report pending unless it returns a receipt.", tools: [tool]);
        await agent.RunAsync("Check the approved refund.");
        return result ?? throw new InvalidOperationException("Agent did not invoke the tool.");
    }
}
