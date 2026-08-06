using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using System.Net.Http.Headers;
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
    /// <summary>Сколько ждать заголовков ответа, прежде чем считать источник недоступным.</summary>
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Сколько молчания в уже открытом потоке считается обрывом.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Как часто обновляется индикатор загрузки.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private const int AttemptsPerSource = 3;

    /// <summary>
    /// Ниже какой средней скорости источник считается безнадёжным.
    /// </summary>
    /// <remarks>
    /// Обрыв ловится таймаутом молчания, но замедление до килобайта в секунду
    /// формально остаётся работающей загрузкой: данные идут, а модель на сотни
    /// мегабайт качалась бы сутки. Порог заведомо ниже любого обычного канала,
    /// чтобы не мешать медленному, но пригодному соединению.
    /// </remarks>
    private const long MinimumBytesPerSecond = 16 * 1024;

    /// <summary>Сколько ждать, прежде чем судить о скорости источника.</summary>
    private static readonly TimeSpan SpeedGracePeriod = TimeSpan.FromSeconds(90);

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

        if (ClearMissing(settings.Streaming.RussianModelPath, Directory.Exists))
        {
            settings.Streaming.RussianModelPath = string.Empty;
            changed = true;
        }

        if (ClearMissing(settings.Streaming.EnglishModelPath, Directory.Exists))
        {
            settings.Streaming.EnglishModelPath = string.Empty;
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
                if (string.IsNullOrWhiteSpace(settings.Streaming.EnglishModelPath))
                {
                    settings.Streaming.EnglishModelPath = path;
                    changed = true;
                }
            }
            else if (string.IsNullOrWhiteSpace(settings.Streaming.RussianModelPath))
            {
                settings.Streaming.RussianModelPath = path;
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
        var sources = model.DownloadUrls
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed : null)
            .OfType<Uri>()
            .ToList();

        if (sources.Count == 0)
        {
            return ModelInstallResult.Failure("Ссылка на модель должна использовать HTTPS.");
        }

        Directory.CreateDirectory(_paths.ModelsDirectory);
        var targetPath = GetInstallPath(model);
        var tempFile = Path.Combine(_paths.ModelsDirectory, model.Id + ".download");

        try
        {
            await DownloadAsync(model, sources, tempFile, cancellationToken).ConfigureAwait(false);

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
    /// Распаковывает архив модели в её каталог.
    /// </summary>
    /// <remarks>
    /// Движок ожидает каталог с файлами модели. Архивы обычно содержат один
    /// верхний каталог — если так, поднимаем его содержимое на уровень выше,
    /// чтобы путь в настройках указывал прямо на модель.
    ///
    /// Поддерживаются zip и tar.bz2: модели sherpa-onnx распространяются
    /// только вторым форматом. В обоих случаях путь каждой записи проверяется:
    /// архив не должен писать файлы за пределы своего каталога.
    /// </remarks>
    internal static void ExtractArchiveSafely(string archivePath, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);

        if (IsTarBzip2(archivePath))
        {
            ExtractTarBzip2(archivePath, targetDirectory);
        }
        else
        {
            ExtractZip(archivePath, targetDirectory);
        }

        FlattenSingleRootDirectory(targetDirectory);
    }

    /// <summary>Формат определяется по содержимому: расширение временного файла ничего не говорит.</summary>
    private static bool IsTarBzip2(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        Span<byte> magic = stackalloc byte[3];
        return stream.Read(magic) == 3 && magic[0] == (byte)'B' && magic[1] == (byte)'Z' && magic[2] == (byte)'h';
    }

    private static void ExtractZip(string archivePath, string targetDirectory)
    {
        var fullTarget = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
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

    private static void ExtractTarBzip2(string archivePath, string targetDirectory)
    {
        var fullTarget = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

        using var file = File.OpenRead(archivePath);
        using var bzip2 = new BZip2InputStream(file);
        using var tar = TarArchive.CreateInputTarArchive(bzip2, System.Text.Encoding.UTF8);

        // SharpZipLib сам не проверяет выход за пределы каталога, поэтому
        // распаковка идёт через собственный обход записей.
        tar.ProgressMessageEvent += (_, entry, _) =>
        {
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.Name));
            if (!destination.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Архив модели пытается записать файл за пределы каталога: {entry.Name}");
            }
        };

        tar.ExtractContents(targetDirectory);
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

    /// <summary>
    /// Качает модель, перебирая источники и продолжая прерванную загрузку.
    /// </summary>
    /// <remarks>
    /// Раньше загрузка шла одним запросом без ограничения по времени: если
    /// сервер переставал отдавать данные, приложение молча ждало часами, а
    /// обрыв означал загрузку с нуля. Теперь простое соединение обрывается по
    /// таймауту, попытка повторяется с уже скачанного места, а после
    /// нескольких неудач берётся следующий источник.
    /// </remarks>
    private async Task DownloadAsync(
        ModelDescriptor model,
        IReadOnlyList<Uri> sources,
        string tempFile,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var uri in sources)
        {
            for (var attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await DownloadFromSourceAsync(model, uri, tempFile, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    // OperationCanceledException без запроса отмены означает
                    // молчание сервера дольше StallTimeout.
                    lastError = ex;
                    _logger.LogWarning(
                        "Загрузка модели {ModelId} с {Host} прервана (попытка {Attempt} из {Total}): {Error}",
                        model.Id,
                        uri.Host,
                        attempt,
                        AttemptsPerSource,
                        ex.Message);

                    Report(model, ProgressOf(tempFile, model), $"Обрыв связи, повтор {attempt} из {AttemptsPerSource}");
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw lastError ?? new HttpRequestException("Не удалось скачать модель ни с одного источника.");
    }

    private async Task DownloadFromSourceAsync(ModelDescriptor model, Uri uri, string tempFile, CancellationToken cancellationToken)
    {
        // Уже скачанная часть переиспользуется: сервер продолжит с этого места,
        // если поддерживает Range, иначе файл будет перезаписан целиком.
        var existing = File.Exists(tempFile) ? new FileInfo(tempFile).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersTimeout.CancelAfter(HeadersTimeout);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        var received = resumed ? existing : 0;
        var total = resumed
            ? existing + (response.Content.Headers.ContentLength ?? 0)
            : response.Content.Headers.ContentLength ?? model.ApproximateSizeBytes;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            tempFile,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);

        var buffer = new byte[81920];
        var startedAt = DateTimeOffset.UtcNow;
        var startedFrom = received;
        var lastReport = DateTimeOffset.MinValue;

        while (true)
        {
            // Каждое чтение ограничено по времени: иначе замолчавший сервер
            // держал бы загрузку «в процессе» бесконечно.
            using var stallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stallTimeout.CancelAfter(StallTimeout);

            var read = await source.ReadAsync(buffer, stallTimeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            // Событий на каждый блок в 80 КБ набегают десятки тысяч, и очередь
            // диспетчера UI перестаёт успевать — отчёт ограничен по частоте.
            var now = DateTimeOffset.UtcNow;
            if (now - lastReport < ReportInterval)
            {
                continue;
            }

            lastReport = now;
            var elapsed = (now - startedAt).TotalSeconds;
            var speed = elapsed > 0 ? (received - startedFrom) / elapsed : 0;

            // Источник отдаёт настолько медленно, что загрузка не закончится
            // никогда: прерываем, чтобы попробовать следующий.
            if (now - startedAt > SpeedGracePeriod && speed < MinimumBytesPerSecond)
            {
                throw new HttpRequestException(
                    $"Источник {uri.Host} отдаёт около {Megabytes((long)speed)} МБ/с — загрузка прервана.");
            }

            // Загрузка занимает 0.9 шкалы, остальное — проверка и распаковка.
            Report(
                model,
                total > 0 ? Math.Min(0.9, (double)received / total * 0.9) : 0,
                $"Загрузка {Megabytes(received)} из {Megabytes(total)} МБ, {Megabytes((long)speed)} МБ/с");
        }
    }

    private double ProgressOf(string tempFile, ModelDescriptor model)
    {
        if (!File.Exists(tempFile) || model.ApproximateSizeBytes <= 0)
        {
            return 0;
        }

        return Math.Min(0.9, (double)new FileInfo(tempFile).Length / model.ApproximateSizeBytes * 0.9);
    }

    private static string Megabytes(long bytes) => (bytes / 1024.0 / 1024).ToString("0.0");

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
