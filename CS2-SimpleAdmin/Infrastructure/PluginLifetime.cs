namespace CS2_SimpleAdmin.Infrastructure;

/// <summary>
/// One plugin lifetime (Load → Unload). Background work captures the token/generation when it starts;
/// results are applied on the game thread only while the same lifetime is still current.
/// Unload cancels the token and never waits for background work on the game thread.
/// </summary>
internal sealed class PluginLifetime : IDisposable
{
    private static long _nextGeneration;
    private readonly CancellationTokenSource _cts = new();

    public PluginLifetime()
    {
        Generation = Interlocked.Increment(ref _nextGeneration);
    }

    public long Generation { get; }

    public CancellationToken Token => _cts.Token;

    public bool IsAlive => !_cts.IsCancellationRequested;

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
