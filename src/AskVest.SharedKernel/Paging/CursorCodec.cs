using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;

namespace AskVest.SharedKernel;

public static class CursorCodec
{
    private const int MaxEncodedLength = 256;

    public static string Encode(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryDecode(string? encoded, [NotNullWhen(true)] out string? payload)
    {
        payload = null;
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        payload = System.Text.Encoding.UTF8.GetString(bytes);
        return true;
    }
}
