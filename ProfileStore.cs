using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailLoadTester;

/// <summary>
/// Uložení / načtení profilu testu (MailTestOptions) jako JSON.
/// Heslo a proxy heslo se ve výchozím režimu neukládají (bezpečnost).
/// Chybějící nová pole při deserializaci doplní defaulty recordu (měkká migrace).
/// </summary>
public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public sealed record ProfileFile(
        string Version,
        DateTimeOffset SavedAt,
        string? Name,
        MailTestOptions Options);

    public static async Task SaveAsync(string path, MailTestOptions options, string? profileName = null, bool includeSecrets = false, CancellationToken ct = default)
    {
        if (Validation.ContainsPathTraversal(path))
            throw new ArgumentException("Cesta profilu nesmí obsahovat '..'.");
        var safe = includeSecrets
            ? options
            : options with { Password = "", ProxyPassword = "", ClientCertificatePassword = "" };

        var file = new ProfileFile(AppVersion.Current, DateTimeOffset.UtcNow, profileName, safe);
        var json = JsonSerializer.Serialize(file, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    public static async Task<ProfileFile> LoadAsync(string path, CancellationToken ct = default)
    {
        if (Validation.ContainsPathTraversal(path))
            throw new ArgumentException("Cesta profilu nesmí obsahovat '..'.");
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var file = JsonSerializer.Deserialize<ProfileFile>(json, JsonOptions)
                   ?? throw new InvalidOperationException("Neplatný profil (null).");
        if (file.Options is null)
            throw new InvalidOperationException("Profil neobsahuje Options.");
        // Version zůstává z souboru — GUI může zobrazit mismatch vůči AppVersion.Current.
        return file;
    }

    public static bool IsVersionMismatch(ProfileFile file)
        => !string.IsNullOrEmpty(file.Version)
           && !string.Equals(file.Version, AppVersion.Current, StringComparison.Ordinal);
}
