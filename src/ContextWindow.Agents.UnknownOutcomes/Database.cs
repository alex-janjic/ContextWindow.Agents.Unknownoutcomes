using Npgsql;
namespace ContextWindow;

internal static class Database
{
    internal static async Task<NpgsqlConnection> OpenAsync()
    {
        var value = Environment.GetEnvironmentVariable("CW_POSTGRES");
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Set CW_POSTGRES to a disposable PostgreSQL database.");
        var builder = new NpgsqlConnectionStringBuilder(value) { CommandTimeout = 30, Timeout = 10, IncludeErrorDetail = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
    internal static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }
    internal static async Task<int> ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, sql, values);
        return await command.ExecuteNonQueryAsync();
    }
    internal static async Task<T> ScalarAsync<T>(string sql, params object[] values)
    {
        await using var connection = await OpenAsync();
        await using var command = Command(connection, sql, values);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing scalar"));
    }
}
