using System.Text;

namespace DeadCellsMultiplayerMod.Network;

internal enum ProtocolErrorCode
{
    None,
    EmptyInput,
    InvalidLimit,
    Oversized,
    EncodingFailed
}

internal readonly record struct ProtocolEncodeResult(
    bool Success,
    byte[] Bytes,
    ProtocolErrorCode Error);

/// <summary>Shared framing and token rules for the line protocol.</summary>
internal static class ProtocolWire
{
    public static bool TryEncode(string? line, int maxBytes, out byte[] bytes)
    {
        var result = Encode(line, maxBytes);
        bytes = result.Bytes;
        return result.Success;
    }

    public static ProtocolEncodeResult Encode(string? line, int maxBytes)
    {
        if (string.IsNullOrEmpty(line))
            return new(false, Array.Empty<byte>(), ProtocolErrorCode.EmptyInput);
        if (maxBytes <= 0)
            return new(false, Array.Empty<byte>(), ProtocolErrorCode.InvalidLimit);

        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            return bytes.Length <= maxBytes
                ? new(true, bytes, ProtocolErrorCode.None)
                : new(false, Array.Empty<byte>(), ProtocolErrorCode.Oversized);
        }
        catch
        {
            return new(false, Array.Empty<byte>(), ProtocolErrorCode.EncodingFailed);
        }
    }

    public static string SanitizeToken(string? value, int maxLength)
    {
        var safe = (value ?? string.Empty)
            .Replace("|", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        return safe.Length > maxLength ? safe[..maxLength] : safe;
    }
}
