using System.Security.Cryptography;
using System.Text;
using Lumen.Core.Settings;

namespace Lumen.App.Infrastructure;

/// <summary>
/// Encrypts secrets with DPAPI (the Windows Data Protection API).
/// </summary>
/// <remarks>
/// DPAPI derives the key from the user's Windows logon credentials, so we never have to
/// store or manage a key ourselves. The encrypted API key in settings.json can only be
/// decrypted by the same Windows user on the same machine: copying the file elsewhere,
/// or another account reading it, yields nothing useful. The extra "entropy" bytes tie
/// the blob to Lumen, so other programs running as the same user cannot decrypt it by
/// accident with a plain DPAPI call.
/// </remarks>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Lumen.ApiKey.v1"u8.ToArray();

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public string? Unprotect(string protectedText)
    {
        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
