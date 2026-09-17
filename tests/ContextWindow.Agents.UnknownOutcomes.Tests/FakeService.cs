using System.Net;
using System.Text;
namespace ContextWindow.Tests;

internal static class FakeService
{
    internal static async Task RunAsync(string address)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(address);
        listener.Start();
        Console.WriteLine("READY");
        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleAsync(context));
        }
    }
    private static async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var write = Json.Decode<Write>(await reader.ReadToEndAsync());
            var lookup = context.Request.Url!.AbsolutePath == "/lookup";
            await Database.ExecuteAsync("INSERT INTO unknown_downstream.requests(tenant,operation,kind) VALUES ($1,$2,$3)", write.Tenant, write.OperationId, lookup ? "lookup" : "write");
            Receipt? receipt = null;
            if (lookup)
            {
                if (context.Request.QueryString["miss"] != "True")
                {
                    await using var connection = await Database.OpenAsync();
                    await using var command = Database.Command(connection, "SELECT payload FROM unknown_downstream.mutations WHERE tenant=$1 AND operation=$2 ORDER BY receipt LIMIT 1", write.Tenant, write.OperationId);
                    var value = await command.ExecuteScalarAsync();
                    if (value is string json)
                        receipt = Json.Decode<Receipt>(json);
                }
            }
            else
            {
                var mode = Enum.Parse<Capability>(context.Request.QueryString["mode"]!);
                // Separate connection/transaction: downstream cannot commit the local ledger.
                await using var connection = await Database.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                if (mode == Capability.Idempotency)
                {
                    await using var gate = Database.Command(connection, "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", Json.Encode(new
                    {
                        write.Tenant,
                        write.OperationId
                    }));
                    await gate.ExecuteNonQueryAsync();
                    await using var find = Database.Command(connection, "SELECT m.payload,k.args FROM unknown_downstream.keys k JOIN unknown_downstream.mutations m ON m.receipt=k.receipt WHERE k.tenant=$1 AND k.operation=$2", write.Tenant, write.OperationId);
                    await using var existing = await find.ExecuteReaderAsync();
                    if (await existing.ReadAsync())
                    {
                        if (existing.GetString(1) != write.Arguments)
                            throw new InvalidOperationException("Changed key arguments");
                        receipt = Json.Decode<Receipt>(existing.GetString(0));
                    }
                }
                if (receipt is null)
                {
                    receipt = new Receipt(Guid.NewGuid().ToString("N"), write);
                    await using var insert = Database.Command(connection, "INSERT INTO unknown_downstream.mutations VALUES ($1,$2,$3,$4)", receipt.Id, write.Tenant, write.OperationId, Json.Encode(receipt));
                    await insert.ExecuteNonQueryAsync();
                    if (mode == Capability.Idempotency)
                    {
                        await using var key = Database.Command(connection, "INSERT INTO unknown_downstream.keys VALUES ($1,$2,$3,$4)", write.Tenant, write.OperationId, write.Arguments, receipt.Id);
                        await key.ExecuteNonQueryAsync();
                    }
                }
                await transaction.CommitAsync();
                if (context.Request.QueryString["drop"] == "True")
                {
                    // Commit is durable. Truncate the response, not the request.
                    context.Response.ContentLength64 = 1000;
                    await context.Response.OutputStream.WriteAsync(new byte[] { (byte)'{' });
                    context.Response.Abort();
                    return;
                }
                if (context.Request.QueryString["bad"] == "True")
                    receipt = receipt with
                    {
                        Write = write with
                        {
                            OperationId = "wrong-operation"
                        }
                    };
            }
            var bytes = Encoding.UTF8.GetBytes(Json.Encode(receipt));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Downstream error type: " + exception.GetType().Name);
            context.Response.Abort();
        }
    }
}
