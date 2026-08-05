using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceFlowWin.Updater;

/// <summary>
/// Содержимое update-manifest.json, который release-workflow кладёт рядом
/// с артефактами релиза.
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("minimumSupportedVersion")]
    public string MinimumSupportedVersion { get; set; } = "0.0.0";

    [JsonPropertyName("releaseDate")]
    public DateTimeOffset ReleaseDate { get; set; }

    [JsonPropertyName("installerUrl")]
    public string InstallerUrl { get; set; } = string.Empty;

    [JsonPropertyName("portableUrl")]
    public string PortableUrl { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    [JsonPropertyName("releaseNotesUrl")]
    public string ReleaseNotesUrl { get; set; } = string.Empty;

    /// <summary>Подпись отсутствует — сборка выпущена без сертификата.</summary>
    [JsonPropertyName("unsigned")]
    public bool Unsigned { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static UpdateManifest? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Проверяет, что о таком обновлении можно хотя бы сообщить пользователю.
    /// </summary>
    /// <remarks>
    /// Манифест, собранный из GitHub Releases API, не содержит контрольной
    /// суммы: она нужна для автоматической загрузки, но не для уведомления со
    /// ссылкой на страницу релиза. Требовать её здесь значило бы молча
    /// проглатывать выпуск, у которого не оказалось update-manifest.json.
    /// </remarks>
    public bool CanNotify(out string error)
    {
        if (!SemanticVersion.TryParse(Version, out _))
        {
            error = "Манифест не содержит корректной версии.";
            return false;
        }

        if (!IsHttps(InstallerUrl))
        {
            error = "Ссылка на установщик должна использовать HTTPS.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Проверяет, что манифест пригоден для автоматической загрузки.</summary>
    public bool IsValid(out string error)
    {
        if (!CanNotify(out error))
        {
            return false;
        }

        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            error = "Манифест не содержит корректной контрольной суммы SHA-256.";
            return false;
        }

        if (Size <= 0)
        {
            error = "Манифест не содержит размера пакета.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
