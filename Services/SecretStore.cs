using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutoPrompt.Services;

internal sealed class SecretStore
{
    public bool HasKey => File.Exists(AppPaths.SecretFile);

    public void Save(string value)
    {
        AppPaths.EnsureDirectories();
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var temporary = AppPaths.SecretFile + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, AppPaths.SecretFile, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public string? Load()
    {
        if (!File.Exists(AppPaths.SecretFile))
            return null;

        var encrypted = File.ReadAllBytes(AppPaths.SecretFile);
        var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete()
    {
        if (File.Exists(AppPaths.SecretFile))
            File.Delete(AppPaths.SecretFile);
    }
}
