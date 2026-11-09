namespace Shorty.Core;

/// <summary>
/// A keyed permutation of [0, <see cref="Space"/>): every number maps to exactly one other number and back, so two
/// different ids can never produce the same code, yet consecutive ids look unrelated.
/// </summary>
/// <remarks>
/// A 4-round Feistel network over 42 bits (2^42 is just above the 62^7 codes that fit in seven characters), with cycle
/// walking to stay inside the code space: a result that falls outside is run through the network again, which keeps the
/// mapping a permutation (this is how format-preserving encryption handles awkward domain sizes). The expected number of
/// rounds is about 1.25.
/// <para>
/// This hides the creation order and the volume. It is obfuscation, <b>not encryption</b>: the round function is a fast
/// integer mixer, not a cryptographic one, so do not rely on it to keep a link secret.
/// </para>
/// </remarks>
public sealed class CodeScrambler
{
    public const int CodeLength = 7;

    /// <summary>62^7 = 3,521,614,606,208 codes.</summary>
    public static readonly ulong Space = Base62.Capacity(CodeLength);

    private const int HalfBits = 21;
    private const ulong HalfMask = (1UL << HalfBits) - 1;
    private const int Rounds = 4;

    private readonly ulong[] _roundKeys = new ulong[Rounds];

    public CodeScrambler(ulong seed)
    {
        for (var i = 0; i < Rounds; i++)
            _roundKeys[i] = Mix(seed + ((ulong)(i + 1) * 0x9E3779B97F4A7C15UL));
    }

    public ulong Scramble(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, Space);

        var result = Encrypt(value);
        while (result >= Space) result = Encrypt(result);
        return result;
    }

    public ulong Unscramble(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, Space);

        var result = Decrypt(value);
        while (result >= Space) result = Decrypt(result);
        return result;
    }

    private ulong Encrypt(ulong value)
    {
        var left = value >> HalfBits;
        var right = value & HalfMask;
        for (var i = 0; i < Rounds; i++)
            (left, right) = (right, left ^ RoundFunction(right, _roundKeys[i]));
        return (left << HalfBits) | right;
    }

    private ulong Decrypt(ulong value)
    {
        var left = value >> HalfBits;
        var right = value & HalfMask;
        for (var i = Rounds - 1; i >= 0; i--)
            (left, right) = (right ^ RoundFunction(left, _roundKeys[i]), left);
        return (left << HalfBits) | right;
    }

    private static ulong RoundFunction(ulong half, ulong roundKey) => Mix(half ^ roundKey) & HalfMask;

    // The finaliser of splitmix64: cheap, and every input bit affects every output bit.
    private static ulong Mix(ulong x)
    {
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }
}
