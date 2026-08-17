using System.Text;

namespace DeadCellsMultiplayerMod.Network;

/// <summary>Shared framing and token rules for the line protocol.</summary>
internal static class ProtocolWire
{
    public static bool TryEncode(string? line, int maxBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(line) || maxBytes <= 0)
            return false;

        bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= maxBytes)
            return true;

        bytes = Array.Empty<byte>();
        return false;
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
