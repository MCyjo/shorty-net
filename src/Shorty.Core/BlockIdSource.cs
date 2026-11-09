namespace Shorty.Core;

/// <summary>Reserves ranges of ids in a shared store, so that no two instances ever receive the same range.</summary>
public interface IIdBlockStore
{
    /// <returns>The first id of a block of <paramref name="size"/> consecutive ids that nobody else will receive.</returns>
    Task<long> ReserveBlockAsync(int size, CancellationToken ct);
}

/// <summary>
/// Hands out unique ids from a block reserved in the shared store. Reserving needs one round trip per
/// <c>blockSize</c> ids, and between those the ids are handed out from memory without any coordination.
/// </summary>
/// <remarks>
/// A process that stops forgets the rest of its block, so ids are unique but not gap-free. That is fine: the id
/// is only ever fed to the scrambler, and nothing relies on ids being consecutive.
/// </remarks>
public sealed class BlockIdSource(IIdBlockStore store, int blockSize) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refill = new(1, 1);
    private long _next;
    private long _end;

    public async ValueTask<long> NextIdAsync(CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);

        while (true)
        {
            lock (_gate)
            {
                if (_next < _end) return _next++;
            }

            await _refill.WaitAsync(ct);
            try
            {
                bool exhausted;
                lock (_gate) exhausted = _next >= _end;
                if (!exhausted) continue; // another caller refilled while this one was waiting

                var start = await store.ReserveBlockAsync(blockSize, ct);
                lock (_gate)
                {
                    _next = start;
                    _end = start + blockSize;
                }
                ShortyMetrics.BlocksAllocated.Add(1);
            }
            finally
            {
                _refill.Release();
            }
        }
    }

    public void Dispose() => _refill.Dispose();
}
