using System.Diagnostics;
namespace ContextWindow.Tests;

internal sealed class Child : IDisposable
{
    private readonly Process process;
    private readonly Task<string> error;
    private bool disposed;
    private Child(Process process)
    {
        this.process = process;
        error = process.StandardError.ReadToEndAsync();
    }
    internal static long LargestSampledWorkingSet
    {
        get; private set;
    }
    internal static Child Start(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return new Child(Process.Start(info) ?? throw new InvalidOperationException("Could not start fixture process"));
    }
    internal async Task<string> UntilAsync(string prefix)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (!process.HasExited)
                    LargestSampledWorkingSet = Math.Max(LargestSampledWorkingSet, process.WorkingSet64);
                return line[prefix.Length..];
            }
        }
        throw new InvalidOperationException("Child ended before " + prefix + ": " + await error);
    }
    internal async Task WaitAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Child failed: " + await error);
    }
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000);
        }
        process.Dispose();
    }
}
