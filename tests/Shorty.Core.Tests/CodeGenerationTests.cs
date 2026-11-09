using Shorty.Core;

namespace Shorty.Core.Tests;

public class Base62Tests
{
    [Fact]
    public void Capacity_OfSevenCharacters_Is62ToTheSeventh() =>
        Assert.Equal(3_521_614_606_208UL, Base62.Capacity(7));

    [Theory]
    [InlineData(0UL, "0000000")]
    [InlineData(61UL, "000000z")]
    [InlineData(62UL, "0000010")]
    public void Encode_PadsToTheFixedWidth(ulong value, string expected) =>
        Assert.Equal(expected, Base62.Encode(value, 7));

    [Fact]
    public void EncodeThenDecode_ReturnsTheOriginalValue()
    {
        foreach (var value in new[] { 0UL, 1UL, 61UL, 62UL, 123_456_789UL, Base62.Capacity(7) - 1 })
            Assert.Equal(value, Base62.Decode(Base62.Encode(value, 7)));
    }

    [Fact]
    public void Encode_RejectsAValueThatDoesNotFit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Base62.Encode(Base62.Capacity(7), 7));

    [Fact]
    public void Decode_RejectsCharactersOutsideTheAlphabet() =>
        Assert.Throws<FormatException>(() => Base62.Decode("ab+cd"));
}

public class CodeScramblerTests
{
    private static readonly CodeScrambler Scrambler = new(seed: 0xC0FFEE);

    [Fact]
    public void ConsecutiveIds_NeverShareACode()
    {
        // One million consecutive ids: a single repeated output would mean two links get the same address.
        var seen = new HashSet<ulong>();
        for (ulong id = 0; id < 1_000_000; id++)
        {
            var code = Scrambler.Scramble(id);
            Assert.True(code < CodeScrambler.Space);
            Assert.True(seen.Add(code), $"id {id} produced a code that was already produced");
        }
    }

    [Fact]
    public void Unscramble_UndoesScramble_IncludingTheEdgesOfTheSpace()
    {
        var random = new Random(42);
        var ids = Enumerable.Range(0, 100_000).Select(_ => (ulong)random.NextInt64(0, (long)CodeScrambler.Space))
            .Concat([0UL, 1UL, CodeScrambler.Space - 1, CodeScrambler.Space - 2]);

        foreach (var id in ids)
            Assert.Equal(id, Scrambler.Unscramble(Scrambler.Scramble(id)));
    }

    [Fact]
    public void ScramblingAValueOutsideTheSpace_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Scrambler.Scramble(CodeScrambler.Space));

    [Fact]
    public void DifferentSeeds_GiveDifferentMappings()
    {
        var other = new CodeScrambler(seed: 0xBADF00D);

        var differing = Enumerable.Range(0, 100).Count(i => Scrambler.Scramble((ulong)i) != other.Scramble((ulong)i));

        Assert.True(differing >= 99, $"only {differing} of 100 ids changed with a different seed");
    }

    [Fact]
    public void ConsecutiveIds_GiveUnrelatedLookingCodes()
    {
        // Random 7-character codes differ in about 6.9 positions. A counter would differ in one.
        var totalDifferences = 0;
        const int pairs = 10_000;
        for (ulong id = 0; id < pairs; id++)
        {
            var a = Base62.Encode(Scrambler.Scramble(id), CodeScrambler.CodeLength);
            var b = Base62.Encode(Scrambler.Scramble(id + 1), CodeScrambler.CodeLength);
            totalDifferences += a.Zip(b).Count(p => p.First != p.Second);
        }

        var average = totalDifferences / (double)pairs;
        Assert.True(average > 6.0, $"consecutive codes differ in only {average:0.0} of 7 positions on average");
    }

    [Fact]
    public void Codes_UseTheWholeAlphabetEvenlyInTheFirstPosition()
    {
        // Cycle walking keeps the mapping uniform over the full 62^7 space. Without it the first character could only
        // take about 20 values, which would leak the structure of the encoding.
        var counts = new int[62];
        const int samples = 124_000;
        for (ulong id = 0; id < samples; id++)
            counts[Base62.Alphabet.IndexOf(Base62.Encode(Scrambler.Scramble(id), 7)[0], StringComparison.Ordinal)]++;

        Assert.All(counts, c => Assert.InRange(c, samples / 62 * 0.8, samples / 62 * 1.2)); // expected 2000 each
    }
}

public class ShortCodeGeneratorTests
{
    private static readonly ShortCodeGenerator Generator = new(new CodeScrambler(seed: 7));

    [Fact]
    public void Codes_HaveSevenCharactersFromTheAlphabet()
    {
        foreach (var id in new long[] { 0, 1, 999, 1_000_000, 3_521_614_606_207 })
        {
            var code = Generator.FromId(id);
            Assert.Equal(7, code.Length);
            Assert.All(code, c => Assert.Contains(c, Base62.Alphabet));
        }
    }

    [Fact]
    public void AnIdBeyondTheCodeSpace_IsReportedAsExhaustion() =>
        Assert.Throws<InvalidOperationException>(() => Generator.FromId(3_521_614_606_208));

    [Fact]
    public void ANegativeId_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Generator.FromId(-1));
}
