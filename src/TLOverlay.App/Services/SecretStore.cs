using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace TLOverlay.App.Services;

/// <summary>
/// Keeps API keys out of settings.json in readable form.
///
/// Encrypted with DPAPI under the current Windows user, so what is on disk is
/// useless on another machine or to another account. This is not a vault - the
/// same user's own processes can undo it, and nothing can change that for a key
/// this app has to send - but a settings file that gets copied into a bug
/// report, a backup, or a shared folder should not hand over someone's billing
/// credentials, and that is the case worth defending against.
/// </summary>
public static class SecretStore
{
    /// <summary>Marks a value this class wrote, so a hand-typed key still works.</summary>
    private const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = "TLOverlay.SecretStore.v1"u8.ToArray();

    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return null;
        }

        try
        {
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText),
                Entropy,
                DataProtectionScope.CurrentUser);

            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (CryptographicException ex)
        {
            // Storing it in the clear would be worse than not storing it, so the
            // player is asked to type it again next time rather than told nothing.
            Log.Error(ex, "Could not encrypt a secret; it will not be saved.");
            return null;
        }
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // A key someone put into settings.json by hand. Honour it: refusing
            // would look like the app losing their key.
            return stored;
        }

        try
        {
            byte[] decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Prefix.Length..]),
                Entropy,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Written by a different Windows account, or the profile was rebuilt.
            Log.Warning(ex, "A stored secret could not be decrypted on this account.");
            return null;
        }
    }

    /// <summary>
    /// What to show in a password box for a key that is already stored: enough
    /// to recognise, never enough to use.
    /// </summary>
    public static string Mask(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return string.Empty;
        }

        string text = plainText.Trim();

        return text.Length <= 8
            ? new string('•', text.Length)
            : $"{text[..4]}{new string('•', 8)}{text[^4..]}";
    }
}
