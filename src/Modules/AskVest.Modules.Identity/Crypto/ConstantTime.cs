using System.Security.Cryptography;
using System.Text;

namespace AskVest.Modules.Identity.Crypto;

public static class ConstantTime
{
    public static bool Equals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public static bool Equals(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Length == right.Length
            && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
