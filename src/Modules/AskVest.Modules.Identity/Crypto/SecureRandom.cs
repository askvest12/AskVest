using System.Security.Cryptography;
using System.Text;

namespace AskVest.Modules.Identity.Crypto;

public interface ISecureRandom
{
    string NewToken(int byteLength = 32);

    string NewOneTimeCode(int digits = 6);
}

public sealed class SecureRandom : ISecureRandom
{
    public string NewToken(int byteLength = 32)
    {
        var bytes = new byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public string NewOneTimeCode(int digits = 6)
    {
        var upperBound = (int)Math.Pow(10, digits);
        var value = RandomNumberGenerator.GetInt32(0, upperBound);
        return value.ToString($"D{digits}", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
