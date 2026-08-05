using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoiceFlowWin.Updater;

public readonly record struct VerificationResult(bool IsValid, string? Error = null)
{
    public static readonly VerificationResult Success = new(true);

    public static VerificationResult Failure(string error) => new(false, error);
}

/// <summary>
/// Проверяет скачанный пакет перед запуском.
/// </summary>
/// <remarks>
/// Непроверенный файл не запускается никогда. Проверок три:
///
/// 1. Размер и SHA-256 — от битой или подменённой загрузки.
/// 2. Имя файла — только простое имя без разделителей пути, чтобы ссылка вида
///    <c>../../Windows/System32/...</c> не увела запись за пределы каталога
///    обновлений.
/// 3. Расположение — итоговый путь обязан лежать внутри каталога обновлений.
///
/// Цифровая подпись проверяется дополнительно, если сборка подписана.
/// </remarks>
public sealed class PackageVerifier
{
    private readonly ILogger<PackageVerifier> _logger;

    public PackageVerifier(ILogger<PackageVerifier>? logger = null) =>
        _logger = logger ?? NullLogger<PackageVerifier>.Instance;

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>Безопасное имя файла из URL: без путей, без обхода каталога.</summary>
    public static bool TryGetSafeFileName(string url, out string fileName)
    {
        fileName = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var candidate = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Contains("..", StringComparison.Ordinal) ||
            candidate.Contains('/') ||
            candidate.Contains('\\') ||
            candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        // Запускать разрешено только установщик.
        if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fileName = candidate;
        return true;
    }

    /// <summary>Проверяет, что путь не вышел за пределы каталога обновлений.</summary>
    public static bool IsInsideDirectory(string filePath, string directory)
    {
        var fullFile = Path.GetFullPath(filePath);
        var fullDirectory = Path.GetFullPath(directory);

        if (!fullDirectory.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDirectory += Path.DirectorySeparatorChar;
        }

        return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public VerificationResult Verify(string filePath, UpdateManifest manifest, string updatesDirectory)
    {
        if (!File.Exists(filePath))
        {
            return VerificationResult.Failure("Файл обновления не найден.");
        }

        if (!IsInsideDirectory(filePath, updatesDirectory))
        {
            return VerificationResult.Failure("Файл обновления находится вне каталога обновлений.");
        }

        var info = new FileInfo(filePath);
        if (info.Length != manifest.Size)
        {
            return VerificationResult.Failure($"Размер пакета не совпадает: ожидалось {manifest.Size}, получено {info.Length}.");
        }

        var actual = ComputeSha256(filePath);
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return VerificationResult.Failure("Контрольная сумма SHA-256 не совпадает.");
        }

        if (!manifest.Unsigned && OperatingSystem.IsWindows() && !HasValidSignature(filePath))
        {
            return VerificationResult.Failure("Цифровая подпись установщика недействительна.");
        }

        return VerificationResult.Success;
    }

    [SupportedOSPlatform("windows")]
    private bool HasValidSignature(string filePath)
    {
        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            return certificate.Verify();
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Не удалось проверить подпись файла {File}.", filePath);
            return false;
        }
    }
}
