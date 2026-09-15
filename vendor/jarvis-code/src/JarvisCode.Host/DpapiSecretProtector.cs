using System.Security.Cryptography;
using System.Text;
using JarvisCode.Core.Settings;

namespace JarvisCode.Host;

/// <summary>Encrypts API keys at rest with Windows DPAPI, scoped to the current user.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string plaintext)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public string? Unprotect(string ciphertext)
    {
        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(ciphertext), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Settings copied from another machine/user can't be decrypted; the key
            // is simply treated as absent so the user re-enters it.
            return null;
        }
    }
}
