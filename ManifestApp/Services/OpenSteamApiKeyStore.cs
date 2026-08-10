using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using ManifestApp.Core;

namespace ManifestApp.Services;

/// <summary>
/// Stores the OpenSteam API key on disk using Windows DPAPI, scoped to the current Windows user.
/// </summary>
internal static class OpenSteamApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("gamegen-app/gamegen-api/credential_v1");

    private static string FilePath => Path.Combine(AppPaths.LocalRoot, "opensteam_api_key.bin");
    private static string LegacyFilePath => Path.Combine(AppPaths.LocalRoot, "gamegen_api_key.bin");

    internal static bool TryRetrieve([NotNullWhen(true)] out string? apiKey)
    {
        apiKey = null;

        foreach (var path in new[] { FilePath, LegacyFilePath })
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var ciphertext = File.ReadAllBytes(path);
                if (ciphertext.Length == 0)
                    continue;

                var plaintext = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
                var s = Encoding.UTF8.GetString(plaintext);
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                apiKey = s.Trim();
                if (!string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    try { Replace(apiKey); } catch { /* migration is best-effort */ }
                }

                return true;
            }
            catch
            {
                // try next path / legacy vault
            }
        }

        if (TryRetrieveFromPasswordVault(out var legacy) && !string.IsNullOrWhiteSpace(legacy))
        {
            try { Replace(legacy.Trim()); } catch { /* migration is best-effort */ }
            apiKey = legacy.Trim();
            return true;
        }

        return false;
    }

    internal static void Replace(string plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey))
            throw new ArgumentException("API key cannot be empty.", nameof(plainTextKey));

        AppPaths.EnsureLayout();

        var plaintext = Encoding.UTF8.GetBytes(plainTextKey.Trim());
        var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, ciphertext);
        File.Move(tmp, FilePath, overwrite: true);

        TryDeleteLegacyVaultEntry();
        try
        {
            if (File.Exists(LegacyFilePath))
                File.Delete(LegacyFilePath);
        }
        catch { /* best effort */ }
    }

    private const string LegacyVaultResource = "gamegen-app/gamegen-api";
    private const string LegacyVaultResourceOlder = "manifestapp/gamegen-api";
    private const string LegacyVaultUser = "credential_v1";

    private static bool TryRetrieveFromPasswordVault([NotNullWhen(true)] out string? apiKey)
    {
        apiKey = null;
        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            foreach (var c in vault.RetrieveAll())
            {
                if (!string.Equals(c.UserName, LegacyVaultUser, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(c.Resource, LegacyVaultResource, StringComparison.Ordinal)
                    && !string.Equals(c.Resource, LegacyVaultResourceOlder, StringComparison.Ordinal))
                    continue;

                c.RetrievePassword();
                if (!string.IsNullOrWhiteSpace(c.Password))
                {
                    apiKey = c.Password;
                    return true;
                }
            }
        }
        catch
        {
            // Vault unavailable / empty — no legacy data to migrate.
        }

        return false;
    }

    private static void TryDeleteLegacyVaultEntry()
    {
        try
        {
            var vault = new Windows.Security.Credentials.PasswordVault();
            foreach (var c in vault.RetrieveAll().ToList())
            {
                if (!string.Equals(c.UserName, LegacyVaultUser, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(c.Resource, LegacyVaultResource, StringComparison.Ordinal)
                    && !string.Equals(c.Resource, LegacyVaultResourceOlder, StringComparison.Ordinal))
                    continue;

                try { vault.Remove(c); } catch { /* ignore */ }
            }
        }
        catch
        {
            // Vault unreachable — there's nothing to clean up, that's fine.
        }
    }
}
