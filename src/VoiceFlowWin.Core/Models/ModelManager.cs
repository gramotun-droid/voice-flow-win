using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Core.Models;

public sealed class ModelProgressEventArgs : EventArgs
{
    public ModelProgressEventArgs(string modelId, double fraction, string stage)
    {
        ModelId = modelId;
        Fraction = fraction;
        Stage = stage;
    }

    public string ModelId { get; }

    public double Fraction { get; }

    public string Stage { get; }
}

public readonly record struct ModelInstallResult(bool Success, string? Path, string? Error)
{
    public static ModelInstallResult Ok(string path) => new(true, path, null);

    public static ModelInstallResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Скачивает, проверяет и устанавливает модели распознавания.
/// </summary>
/// <remarks>
/// Распаковка архива — самое опасное место: запись по пути из архива позволяет
/// выйти за пределы каталога моделей, если внутри лежит запись вида
/// <c>../../windows/system32/…</c>. Поэтому каждая запись проверяется на
/// принадлежность целевому каталогу, и только после этого создаётся файл.
/// Установленная модель никогда не удаляется автоматически: обновление
/// приложения не должно заставлять пользователя качать гигабайты заново.
/// </remarks>
public sealed class ModelManager
{
    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;
    private readonly ILogger<ModelManager> _logger;

    public ModelManager(HttpClient httpClient, AppPaths paths, ILogger<ModelManager>? logger = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _logger = logger ?? NullLogger<ModelManager>.Instance;
    }

    public event EventHandler<ModelProgressEventArgs>? Progress;

    public string GetInstallPath(ModelDescriptor model) => model.IsArchive
        ? Path.Combine(_paths.ModelsDirectory, model.Id)
        : Path.Combine(_paths.ModelsDirectory, model.Id + ".bin");

    public bool IsInstalled(ModelDescriptor model)
    {
        var path = GetInstallPath(model);
        return model.IsArchive ? Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() : File.Exists(path);
    }

    /// <summary>
    /// Приводит пути моделей в настройках в соответствие с тем, что реально
    /// лежит на диске.
    /// </summary>
    /// <remarks>
    /// Скачанная модель бесполезна, пока движок не знает пути к ней, а путь
    /// удалённой модели ронял бы распознавание при следующем запуске. Поэтому
    /// пустой путь заполняется установленной моделью, а путь, которого больше
    /// нет на диске, очищается. Уже заданный рабочий путь не трогается: выбор
    /// между компактной и полной моделью остаётся за пользователем.
    /// </remarks>
    /// <returns><c>true</c>, если настройки изменились и их нужно сохранить.</returns>
    public bool SynchronizeInstalledPaths(AppSettings settings)
    {
        var changed = false;

        if (ClearMissing(settings.Vosk.RussianModelPath, Directory.Exists))
        {
            settings.Vosk.RussianModelPath = string.Empty;
            changed = true;
        }

        if (ClearMissing(settings.Vosk.EnglishModelPath, Directory.Exists))
        {
            settings.Vosk.EnglishModelPath = string.Empty;
            changed = true;
        }

        if (ClearMissing(settings.Whisper.ModelPath, File.Exists))
        {
            settings.Whisper.ModelPath = string.Empty;
            settings.Whisper.ModelId = string.Empty;
            changed = true;
        }

        foreach (var model in ModelCatalog.All)
        {
            if (!IsInstalled(model))
            {
                continue;
            }

            var path = GetInstallPath(model);
            if (model.Kind == ModelKind.Whisper)
            {
                if (string.IsNullOrWhiteSpace(settings.Whisper.ModelPath))
                {
                    settings.Whisper.ModelPath = path;
                    settings.Whisper.ModelId = model.Id;
                    changed = true;
                }
            }
            else if (model.Language == RecognitionLanguage.English)
            {
                if (string.IsNullOrWhiteSpace(settings.Vosk.EnglishModelPath))
                {
                    settings.Vosk.EnglishModelPath = path;
                    changed = true;
                }
            }
            else if (string.IsNullOrWhiteSpace(settings.Vosk.RussianModelPath))
            {
                settings.Vosk.RussianModelPath = path;
                changed = true;
            }
        }

        if (changed)
        {
            _logger.LogInformation("Пути моделей в настройках синхронизированы с каталогом моделей.");
        }

        return changed;
    }

    private static bool ClearMissing(string path, Func<string, bool> exists) =>
        !string.IsNullOrWhiteSpace(path) && !exists(path);

    public async Task<ModelInstallResult> InstallAsync(ModelDescriptor model, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(model.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return ModelInstallResult.Failure("Ссылка на модель должна использовать HTTPS.");
        }

        Directory.CreateDirectory(_paths.ModelsDirectory);
        var targetPath = GetInstallPath(model);
        var tempFile = Path.Combine(_paths.ModelsDirectory, model.Id + ".download");

        try
        {
            await DownloadAsync(model, uri, tempFile, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(model.Sha256))
            {
                Report(model, 0.9, "Проверка контрольной суммы");
                var actual = await ComputeSha256Async(tempFile, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tempFile);
                    return ModelInstallResult.Failure("Контрольная сумма модели не совпала.");
                }
            }

            if (model.IsArchive)
            {
                Report(model, 0.95, "Распаковка");
                ExtractArchiveSafely(tempFile, targetPath);
                TryDelete(tempFile);
            }
            else
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(tempFile, targetPath);
            }

            Report(model, 1.0, "Готово");
            return ModelInstallResult.Ok(targetPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempFile);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось установить модель {ModelId}.", model.Id);
            TryDelete(tempFile);
            return ModelInstallResult.Failure(ex.Message);
        }
    }

    /// <summary>Удаляет модель только по явной команде пользователя.</summary>
    public bool Remove(ModelDescriptor model)
    {
        var path = GetInstallPath(model);

        try
        {
            if (model.IsArchive && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }

            if (!model.IsArchive && File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось удалить модель {ModelId}.", model.Id);
        }

        return false;
    }

    /// <summary>
    /// Vosk ожидает каталог с файлами модели. Архивы обычно содержат один
    /// верхний каталог — если так, поднимаем его содержимое на уровень выше,
    /// чтобы путь в настройках указывал прямо на модель.
    /// </summary>
    internal static void ExtractArchiveSafely(string archivePath, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);
        var fullTarget = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));

                if (!destination.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Архив модели пытается записать файл за пределы каталога: {entry.FullName}");
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        FlattenSingleRootDirectory(targetDirectory);
    }

    private static void FlattenSingleRootDirectory(string targetDirectory)
    {
        var entries = Directory.GetFileSystemEntries(targetDirectory);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return;
        }

        var inner = entries[0];
        foreach (var item in Directory.GetFileSystemEntries(inner))
        {
            var destination = Path.Combine(targetDirectory, Path.GetFileName(item));
            if (Directory.Exists(item))
            {
                Directory.Move(item, destination);
            }
            else
            {
                File.Move(item, destination);
            }
        }

        Directory.Delete(inner, recursive: true);
    }

    private async Task DownloadAsync(ModelDescriptor model, Uri uri, string tempFile, CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? model.ApproximateSizeBytes;
        long received = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            // Загрузка занимает 0.9 шкалы, остальное — проверка и распаковка.
            Report(model, total > 0 ? Math.Min(0.9, (double)received / total * 0.9) : 0, "Загрузка");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private void Report(ModelDescriptor model, double fraction, string stage) =>
        Progress?.Invoke(this, new ModelProgressEventArgs(model.Id, fraction, stage));

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Временный файл может быть занят антивирусом — не критично.
        }
    }
}
