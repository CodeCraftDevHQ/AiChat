using System.Security.Cryptography;
using System.Text;

namespace AiChat.Services;

/// <summary>Hashing for the app PIN (PBKDF2). The PIN itself is never stored.</summary>
public static class PinHelper
{
    public const int MinLength = 4;
    const int Iterations = 100_000;

    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static string Hash(string pin, string saltBase64) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin), Convert.FromBase64String(saltBase64), Iterations, HashAlgorithmName.SHA256, 32));

    public static bool Verify(string pin, string saltBase64, string expectedHash)
    {
        if (string.IsNullOrEmpty(saltBase64) || string.IsNullOrEmpty(expectedHash)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(Hash(pin, saltBase64)), Convert.FromBase64String(expectedHash));
    }
}
