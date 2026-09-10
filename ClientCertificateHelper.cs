using System.Security.Cryptography.X509Certificates;

namespace MailLoadTester;

public static class ClientCertificateHelper
{
    public static X509Certificate2? Load(string? path, string? password)
    {
        if (string.IsNullOrEmpty(path)) return null;
        // EphemeralKeySet: the private key is kept in memory only for the lifetime
        // of this X509Certificate2 instance and is never persisted to disk/registry
        // by the platform's key store — appropriate for a short-lived load-test
        // client cert that gets reloaded on every test run. Caller must still
        // Dispose() the returned object to release the in-memory key handle.
        const X509KeyStorageFlags flags = X509KeyStorageFlags.EphemeralKeySet;
        return string.IsNullOrEmpty(password)
            ? new X509Certificate2(path, (string?)null, flags)
            : new X509Certificate2(path, password, flags);
    }
}
