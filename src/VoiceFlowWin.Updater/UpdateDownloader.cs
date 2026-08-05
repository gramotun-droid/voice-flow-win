using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoiceFlowWin.Updater;

public sealed class DownloadProgressEventArgs : EventArgs
{
    public DownloadProgressEventArgs(long received, long total)
    {
        Received = received;
        Total = total;
    }

    public long Received { get; }

    public long Total { get; }

    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Received / Total, 0, 1);
}

public readonly record struct DownloadResult(bool Success, string? FilePath, string? Error)
{
    public static DownloadResult Ok(string path) => new(true, path, null);

    public static DownloadResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Скачивает установщик в фоне.
/// </summary>
/// <remarks>
/// Загрузка идёт во временный файл <c>.download</c> и переименовывается в
/// <c>.exe</c> только после успешной проверки: так недокачанный или битый
/// файл физически не может быть запущен. Оборванная загрузка возобновляется
/// через HTTP Range, если сервер это поддерживает, — иначе начинается заново.
/// </remarks>
public sealed class UpdateDownloader
{
    private readonly HttpClient _httpClient;
    private readonly PackageVerifier _verifier;
    private readonly ILogger<UpdateDownloader> _logger;

    public UpdateDownloader(HttpClient httpClient, PackageVerifier verifier, ILogger<UpdateDownloader>? logger = null)
    {
        _httpClient = httpClient;
        _verifier = verifier;
        _logger = logger ?? NullLogger<UpdateDownloader>.Instance;
    }

    public event EventHandler<DownloadProgressEventArgs>? Progress;

    public async Task<DownloadResult> DownloadAsync(
        UpdateManifest manifest,
        string updatesDirectory,
        CancellationToken cancellationToken)
    {
        if (!manifest.IsValid(out var manifestError))
        {
            return DownloadResult.Failure(manifestError);
        }

        if (!PackageVerifier.TryGetSafeFileName(manifest.InstallerUrl, out var fileName))
        {
            return DownloadResult.Failure("Недопустимое имя файла в ссылке на установщик.");
        }

        Directory.CreateDirectory(updatesDirectory);
        var targetPath = Path.Combine(updatesDirectory, fileName);
        var tempPath = targetPath + ".download";

        if (!PackageVerifier.IsInsideDirectory(targetPath, updatesDirectory))
        {
            return DownloadResult.Failure("Путь загрузки выходит за пределы каталога обновлений.");
        }

        // Готовый и проверенный пакет качать заново не нужно.
        if (File.Exists(targetPath) && _verifier.Verify(targetPath, manifest, updatesDirectory).IsValid)
        {
            return DownloadResult.Ok(targetPath);
        }

        try
        {
            await DownloadToTempAsync(manifest, tempPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Загрузка обновления не удалась.");
            return DownloadResult.Failure("Не удалось скачать обновление: " + ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Ошибка записи файла обновления.");
            return DownloadResult.Failure("Не удалось сохранить файл обновления: " + ex.Message);
        }

        var verification = _verifier.Verify(tempPath, manifest, updatesDirectory);
        if (!verification.IsValid)
        {
            // Битую загрузку не оставляем: иначе следующая попытка продолжит её.
            TryDelete(tempPath);
            return DownloadResult.Failure(verification.Error ?? "Проверка пакета не пройдена.");
        }

        TryDelete(targetPath);
        File.Move(tempPath, targetPath);
        return DownloadResult.Ok(targetPath);
    }

    /// <summary>Удаляет пакеты, оставшиеся от прошлых версий.</summary>
    public void CleanupObsoletePackages(string updatesDirectory, string keepFileName)
    {
        if (!Directory.Exists(updatesDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(updatesDirectory))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }
    }

    private async Task DownloadToTempAsync(UpdateManifest manifest, string tempPath, CancellationToken cancellationToken)
    {
        long existingBytes = 0;
        if (File.Exists(tempPath))
        {
            existingBytes = new FileInfo(tempPath).Length;
            if (existingBytes >= manifest.Size)
            {
                // Файл длиннее ожидаемого — значит это мусор от другой версии.
                TryDelete(tempPath);
                existingBytes = 0;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.InstallerUrl);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var resuming = response.StatusCode == HttpStatusCode.PartialContent;
        if (!resuming && existingBytes > 0)
        {
            // Сервер не поддержал докачку — начинаем сначала.
            TryDelete(tempPath);
            existingBytes = 0;
        }

        response.EnsureSuccessStatusCode();

        var total = manifest.Size;
        var received = resuming ? existingBytes : 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            tempPath,
            resuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            Progress?.Invoke(this, new DownloadProgressEventArgs(received, total));
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Не удалось удалить файл {File}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Нет прав на удаление файла {File}.", path);
        }
    }
}
