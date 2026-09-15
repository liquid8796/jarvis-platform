namespace JarvisCode.Core.Settings;

/// <summary>
/// Encrypts secrets (API keys) before they hit disk. The Windows host supplies a
/// DPAPI implementation; tests use the pass-through.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Returns null when the stored value cannot be decrypted (e.g. copied from another machine).</summary>
    string? Unprotect(string ciphertext);
}

public sealed class PassThroughSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;

    public string? Unprotect(string ciphertext) => ciphertext;
}
