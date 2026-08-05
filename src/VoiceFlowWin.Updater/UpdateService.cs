using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Updater;

public enum UpdateState
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    Failed,
}

public sealed record UpdateStatus(
    UpdateState State,
    SemanticVersion? AvailableVersion = null,
    string? PackagePath = null,
    string? Error = null,
    double Progress = 0,
    bool Mandatory = false,
    string? ReleaseNotesUrl = null)
{
    public static readonly UpdateStatus Idle = new(UpdateState.Idle);
}

/// <summary>
/// Проверка, загрузка и подготовка обновлений.
/// </summary>
/// <remarks>
/// Проверка выполняется при каждом запуске и затем каждые несколько часов
/// непрерывной работы. Стартовая проверка асинхронна и никогда не задерживает
/// ни запуск приложения, ни первую диктовку: отсутствие сети — это норма, а не
/// повод показывать ошибку.
///
/// Источник по умолчанию — update-manifest.json из релиза. GitHub Releases API
/// используется только как запасной вариант: он ограничен по числу
/// анонимных запросов, а токен в desktop-приложении храниться не должен.
/// </remarks>
public sealed class UpdateService : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly UpdateDownloader _downloader;
    private readonly PackageVerifier _verifier;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemanticVersion _currentVersion;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;

    public UpdateService(
        HttpClient httpClient,
        UpdateDownloader downloader,
        PackageVerifier verifier,
        ISettingsService settings,
        AppPaths paths,
        SemanticVersion currentVersion,
        ILogger<UpdateService>? logger = null)
    {
        _httpClient = httpClient;
        _downloader = downloader;
        _verifier = verifier;
        _settings = settings;
        _paths = paths;
        _currentVersion = currentVersion;
        _logger = logger ?? NullLogger<UpdateService>.Instance;

        _downloader.Progress += (_, args) =>
            SetStatus(Status with { State = UpdateState.Downloading, Progress = args.Fraction });
    }

    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;

    public SemanticVersion CurrentVersion => _currentVersion;

    public event EventHandler<UpdateStatus>? StatusChanged;

    /// <summary>Готовое к установке обновление, которое пользователь отложил кнопкой «Позже».</summary>
    public bool HasPostponedUpdate { get; private set; }

    /// <summary>Запускает стартовую проверку и периодические проверки.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loopCancellation = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_loopCancellation.Token));
    }

    public async Task<UpdateStatus> CheckNowAsync(CancellationToken cancellationToken)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(new UpdateStatus(UpdateState.Checking));

            var manifest = await FetchManifestAsync(cancellationToken).ConfigureAwait(false);

            var updateSettings = _settings.Current.Updates;
            updateSettings.LastCheckedAt = DateTimeOffset.UtcNow;

            if (manifest is null)
            {
                // Нет сети или манифест недоступен — это не ошибка для пользователя.
                SetStatus(UpdateStatus.Idle);
                return Status;
            }

            if (!manifest.IsValid(out var error))
            {
                _logger.LogWarning("Манифест обновления некорректен: {Error}", error);
                SetStatus(UpdateStatus.Idle);
                return Status;
            }

            if (!ShouldOffer(manifest, updateSettings, out var availableVersion))
            {
                SetStatus(UpdateStatus.Idle);
                return Status;
            }

            SetStatus(new UpdateStatus(
                UpdateState.UpdateAvailable,
                availableVersion,
                Mandatory: manifest.Mandatory,
                ReleaseNotesUrl: manifest.ReleaseNotesUrl));

            if (updateSettings.AutomaticDownload || manifest.Mandatory)
            {
                await DownloadAsync(manifest, cancellationToken).ConfigureAwait(false);
            }

            return Status;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public async Task<UpdateStatus> DownloadAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        SetStatus(Status with { State = UpdateState.Downloading, Progress = 0 });

        var result = await _downloader
            .DownloadAsync(manifest, _paths.UpdatesDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            SetStatus(Status with { State = UpdateState.Failed, Error = result.Error });
            return Status;
        }

        // Более старые пакеты больше не нужны и занимают место.
        _downloader.CleanupObsoletePackages(_paths.UpdatesDirectory, Path.GetFileName(result.FilePath!));

        SetStatus(Status with
        {
            State = UpdateState.ReadyToInstall,
            PackagePath = result.FilePath,
            Progress = 1,
            Error = null,
        });

        HasPostponedUpdate = false;
        return Status;
    }

    /// <summary>«Позже»: файл остаётся на диске, предложение повторится при следующей проверке.</summary>
    public void Postpone()
    {
        if (Status.State == UpdateState.ReadyToInstall)
        {
            HasPostponedUpdate = true;
        }
    }

    /// <summary>Скрытая расширенная настройка: пропустить конкретную необязательную версию.</summary>
    public void SkipVersion(SemanticVersion version)
    {
        if (Status.Mandatory)
        {
            return;
        }

        var settings = _settings.Current;
        settings.Updates.SkippedVersion = version.ToString();
        _settings.Save(settings);
    }

    /// <summary>
    /// Перед установкой пакет проверяется ещё раз: между загрузкой и запуском
    /// файл мог быть подменён или повреждён.
    /// </summary>
    public bool VerifyReadyPackage(UpdateManifest manifest)
    {
        if (Status.PackagePath is null)
        {
            return false;
        }

        var verification = _verifier.Verify(Status.PackagePath, manifest, _paths.UpdatesDirectory);
        if (verification.IsValid)
        {
            return true;
        }

        _logger.LogWarning("Проверка пакета перед установкой не пройдена: {Error}", verification.Error);
        SetStatus(Status with { State = UpdateState.Failed, Error = verification.Error });
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _loopCancellation?.Cancel();

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Штатное завершение.
            }
        }

        _loopCancellation?.Dispose();
        _checkLock.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Стартовая проверка идёт в фоне и не мешает пользователю
            // сразу начать диктовать.
            await CheckNowAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var hours = Math.Max(1, _settings.Current.Updates.CheckIntervalHours);
                await Task.Delay(TimeSpan.FromHours(hours), cancellationToken).ConfigureAwait(false);

                if (Status.State == UpdateState.ReadyToInstall && HasPostponedUpdate)
                {
                    // Пакет уже скачан: просто повторяем предложение.
                    StatusChanged?.Invoke(this, Status);
                    continue;
                }

                await CheckNowAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Выход из приложения.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Цикл проверки обновлений остановлен из-за ошибки.");
        }
    }

    private bool ShouldOffer(UpdateManifest manifest, UpdateSettings updateSettings, out SemanticVersion version)
    {
        version = SemanticVersion.Zero;

        if (!SemanticVersion.TryParse(manifest.Version, out var candidate))
        {
            return false;
        }

        version = candidate;

        // Откат назад и предложение уже установленной версии запрещены.
        if (candidate <= _currentVersion)
        {
            return false;
        }

        // На канале Stable предрелизные сборки не предлагаются.
        if (candidate.IsPreRelease && updateSettings.Channel != UpdateChannel.Beta)
        {
            return false;
        }

        if (!manifest.Mandatory &&
            SemanticVersion.TryParse(updateSettings.SkippedVersion, out var skipped) &&
            skipped == candidate)
        {
            return false;
        }

        return true;
    }

    private async Task<UpdateManifest?> FetchManifestAsync(CancellationToken cancellationToken)
    {
        var updateSettings = _settings.Current.Updates;

        var manifest = await TryFetchManifestAsync(updateSettings.ManifestUrl, cancellationToken).ConfigureAwait(false);
        if (manifest is not null)
        {
            return manifest;
        }

        return await TryFetchFromReleasesApiAsync(updateSettings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateManifest?> TryFetchManifestAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("Адрес манифеста должен использовать HTTPS.");
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var json = await _httpClient.GetStringAsync(uri, timeout.Token).ConfigureAwait(false);
            return UpdateManifest.Deserialize(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Манифест обновления недоступен.");
            return null;
        }
    }

    private async Task<UpdateManifest?> TryFetchFromReleasesApiAsync(UpdateSettings updateSettings, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(updateSettings.ReleasesApiUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var releases = await _httpClient
                .GetFromJsonAsync<List<GitHubRelease>>(uri, timeout.Token)
                .ConfigureAwait(false);

            if (releases is null)
            {
                return null;
            }

            var allowPrerelease = updateSettings.Channel == UpdateChannel.Beta;
            var release = releases
                .Where(item => allowPrerelease || !item.Prerelease)
                .Where(item => !item.Draft)
                .FirstOrDefault();

            if (release is null)
            {
                return null;
            }

            var installer = release.Assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (installer is null)
            {
                return null;
            }

            // API не отдаёт SHA-256, поэтому такой манифест годится только для
            // уведомления: загрузка без контрольной суммы не начнётся.
            return new UpdateManifest
            {
                Version = release.TagName,
                InstallerUrl = installer.BrowserDownloadUrl,
                Size = installer.Size,
                ReleaseNotesUrl = release.HtmlUrl,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            _logger.LogDebug(ex, "GitHub Releases API недоступен.");
            return null;
        }
    }

    private void SetStatus(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    // GitHub отдаёт snake_case, поэтому имена полей заданы явно.
    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size);
}
