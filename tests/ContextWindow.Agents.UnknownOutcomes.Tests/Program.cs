using Microsoft.Extensions.AI;
namespace ContextWindow.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                await Proof.RunAsync(Console.WriteLine);
                return 0;
            }
            if (args[0] == "service")
            {
                await FakeService.RunAsync(args[1]);
                return 0;
            }
            var write = Json.Decode<Write>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[2])));
            using var http = new HttpClient { BaseAddress = new Uri(args[1]), Timeout = TimeSpan.FromSeconds(5) };
            var executor = new Executor(new Ledger(), http, async point =>
            {
                Console.WriteLine($"BARRIER:{point}");
                await Task.Delay(Timeout.InfiniteTimeSpan);
            });
            using var client = new ScriptedClient();
            var result = await RefundTool.RunAsync(client, executor, write, Enum.Parse<Capability>(args[3]), Enum.Parse<Fault>(args[4]));
            Console.WriteLine("RESULT:" + Json.Encode(result));
            return 0;
        }
        catch (Exception exception)
        {
            // Never print database exception messages or connection settings.
            Console.Error.WriteLine("Fixture error: " + exception.GetType().Name);
            return 1;
        }
    }
}
internal sealed class ScriptedClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var hasResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any();
        var message = hasResult ? new ChatMessage(ChatRole.Assistant, "The application displays the structured tool result.") : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), "refund", new Dictionary<string, object?>())]);
        return Task.FromResult(new ChatResponse(message));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose()
    {
    }
}
