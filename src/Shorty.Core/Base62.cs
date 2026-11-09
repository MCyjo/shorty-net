namespace Shorty.Core;

/// <summary>Base62 with a fixed width: digits, then upper case, then lower case. Codes are case sensitive.</summary>
public static class Base62
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Radix = 62;

    /// <summary>Number of values a code of the given length can represent (62^length).</summary>
    public static ulong Capacity(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 10); // 62^10 still fits in 64 bits
        ulong capacity = 1;
        for (var i = 0; i < length; i++) capacity *= Radix;
        return capacity;
    }

    /// <summary>Encodes <paramref name="value"/> in exactly <paramref name="length"/> characters, padded on the left.</summary>
    public static string Encode(ulong value, int length)
    {
        if (value >= Capacity(length))
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Does not fit in {length} Base62 characters.");

        return string.Create(length, value, static (span, v) =>
        {
            for (var i = span.Length - 1; i >= 0; i--)
            {
                span[i] = Alphabet[(int)(v % Radix)];
                v /= Radix;
            }
        });
    }

    public static ulong Decode(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        ulong value = 0;
        foreach (var c in code)
        {
            var digit = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (digit < 0) throw new FormatException($"'{c}' is not a Base62 character.");
            value = checked(value * Radix + (ulong)digit);
        }
        return value;
    }
}
