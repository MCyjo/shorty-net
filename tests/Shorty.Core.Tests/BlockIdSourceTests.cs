using Shorty.Core;

namespace Shorty.Core.Tests;

public class BlockIdSourceTests
{
    /// <summary>Stands in for the shared database counter: hands out consecutive, never overlapping blocks.</summary>
    private sealed class FakeStore : IIdBlockStore
    {
        private long _next;
        private int _reservations;

        public int Reservations => _reservations;

        public Task<long> ReserveBlockAsync(int size, CancellationToken ct)
        {
            Interlocked.Increment(ref _reservations);
            return Task.FromResult(Interlocked.Add(ref _next, size) - size);
        }
    }

    [Fact]
    public async Task IdsWithinABlock_AreConsecutive_AndCostOneReservation()
    {
        var store = new FakeStore();
        using var source = new BlockIdSource(store, blockSize: 100);

        var ids = new List<long>();
        for (var i = 0; i < 100; i++) ids.Add(await source.NextIdAsync());

        Assert.Equal(Enumerable.Range(0, 100).Select(i => (long)i), ids);
        Assert.Equal(1, store.Reservations);
    }

    [Fact]
    public async Task TheFirstIdAfterABlock_ReservesTheNextOne()
    {
        var store = new FakeStore();
        using var source = new BlockIdSource(store, blockSize: 10);

        for (var i = 0; i < 11; i++) await source.NextIdAsync();

        Assert.Equal(2, store.Reservations);
    }

    [Fact]
    public async Task ManyCallersOnOneInstance_GetUniqueIds_WithoutWastingBlocks()
    {
        var store = new FakeStore();
        using var source = new BlockIdSource(store, blockSize: 1000);

        var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            var local = new List<long>(10_000);
            for (var i = 0; i < 10_000; i++) local.Add(await source.NextIdAsync());
            return local;
        })));

        var all = ids.SelectMany(x => x).ToList();
        Assert.Equal(160_000, all.Count);
        Assert.Equal(160_000, all.Distinct().Count());
        Assert.Equal(160, store.Reservations); // 160,000 ids / 1,000 per block: every block was used up before the next
    }

    [Fact]
    public async Task ManyInstancesSharingOneStore_NeverReceiveTheSameId()
    {
        var store = new FakeStore();
        var sources = Enumerable.Range(0, 8).Select(_ => new BlockIdSource(store, blockSize: 500)).ToList();
        try
        {
            // Eight "application instances", each used by four concurrent callers.
            var tasks = sources.SelectMany(source => Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                var local = new List<long>(5_000);
                for (var i = 0; i < 5_000; i++) local.Add(await source.NextIdAsync());
                return local;
            })));

            var all = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

            Assert.Equal(160_000, all.Count);
            Assert.Equal(160_000, all.Distinct().Count());
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
        }
    }
}
