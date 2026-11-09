namespace Shorty.Core;

/// <summary>Turns a unique numeric id into a seven character code. Different ids always give different codes.</summary>
public sealed class ShortCodeGenerator(CodeScrambler scrambler)
{
    public string FromId(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        if ((ulong)id >= CodeScrambler.Space)
            throw new InvalidOperationException("The code space is exhausted: all 62^7 ids have been used.");

        return Base62.Encode(scrambler.Scramble((ulong)id), CodeScrambler.CodeLength);
    }
}
