using System.Net.Http;
using System.Security.Cryptography;
using VoiceFlowWin.Core.Settings;
using VoiceFlowWin.Updater;
using Xunit;

namespace VoiceFlowWin.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.4.2", 1, 4, 2)]
    [InlineData("v1.4.2", 1, 4, 2)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("2.0.0+build.5", 2, 0, 0)]
    public void Разбирает_корректные_версии(string input, int major, int minor, int patch)
    {
        Assert.True(SemanticVersion.TryParse(input, out var version));
        Assert.Equal(new SemanticVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("не версия")]
    [InlineData("1.x.2")]
    public void Отвергает_некорректные_версии(string input) =>
        Assert.False(SemanticVersion.TryParse(input, out _));

    [Fact]
    public void Числовое_сравнение_разрядов()
    {
        // Строковое сравнение поставило бы 1.9.0 выше 1.10.0.
        Assert.True(SemanticVersion.Parse("1.10.0") > SemanticVersion.Parse("1.9.0"));
        Assert.True(SemanticVersion.Parse("2.0.0") > SemanticVersion.Parse("1.99.99"));
    }

    [Fact]
    public void Релиз_старше_своего_предрелиза()
    {
        Assert.True(SemanticVersion.Parse("1.4.0") > SemanticVersion.Parse("1.4.0-beta.1"));
        Assert.True(SemanticVersion.Parse("1.4.0-beta.2") > SemanticVersion.Parse("1.4.0-beta.1"));
        Assert.True(SemanticVersion.Parse("1.4.0-beta.10") > SemanticVersion.Parse("1.4.0-beta.9"));
        Assert.True(SemanticVersion.Parse("1.4.0-beta.1").IsPreRelease);
    }

    [Fact]
    public void Строковое_представление_сохраняет_предрелиз() =>
        Assert.Equal("1.4.0-beta.1", SemanticVersion.Parse("v1.4.0-beta.1").ToString());
}

public class UpdateManifestTests
{
    private static UpdateManifest Valid() => new()
    {
        Version = "1.4.2",
        InstallerUrl = "https://example.com/VoiceFlowWin-Setup-x64-1.4.2.exe",
        Sha256 = new string('A', 64),
        Size = 12345678,
    };

    [Fact]
    public void Корректный_манифест_проходит_проверку()
    {
        Assert.True(Valid().IsValid(out _));
    }

    [Fact]
    public void Ссылка_без_HTTPS_отвергается()
    {
        var manifest = Valid();
        manifest.InstallerUrl = "http://example.com/setup.exe";

        Assert.False(manifest.IsValid(out var error));
        Assert.Contains("HTTPS", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Манифест_без_контрольной_суммы_не_годится_для_загрузки()
    {
        var manifest = Valid();
        manifest.Sha256 = "коротко";

        Assert.False(manifest.IsValid(out _));
    }

    [Fact]
    public void Манифест_без_контрольной_суммы_годится_для_уведомления()
    {
        // Такой манифест собирается из GitHub Releases API, когда в релизе нет
        // update-manifest.json: сообщить о выпуске можно, скачать — нет.
        var manifest = Valid();
        manifest.Sha256 = string.Empty;
        manifest.Size = 0;

        Assert.True(manifest.CanNotify(out _));
        Assert.False(manifest.IsValid(out _));
    }

    [Fact]
    public void Уведомить_о_манифесте_без_версии_нельзя()
    {
        var manifest = Valid();
        manifest.Version = "не версия";

        Assert.False(manifest.CanNotify(out _));
    }

    [Fact]
    public void Сериализация_и_разбор_обратимы()
    {
        var manifest = Valid();
        manifest.Mandatory = true;

        var restored = UpdateManifest.Deserialize(manifest.Serialize());

        Assert.NotNull(restored);
        Assert.Equal(manifest.Version, restored!.Version);
        Assert.True(restored.Mandatory);
    }

    [Fact]
    public void Битый_JSON_не_роняет_разбор() =>
        Assert.Null(UpdateManifest.Deserialize("{ это не json"));
}

/// <summary>
/// Проверяет поведение проверки обновлений без сети: она не должна ни падать,
/// ни терять отметку о времени проверки.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfw-updates-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public UpdateServiceTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Время_проверки_сохраняется_на_диск()
    {
        var settingsService = new SettingsService(_paths);
        var settings = settingsService.Load();

        // Адреса без HTTPS отсекаются до обращения к сети: тест не ходит наружу.
        settings.Updates.ManifestUrl = "file:///нет";
        settings.Updates.ReleasesApiUrl = "file:///нет";
        settingsService.Save(settings);

        var httpClient = new HttpClient();
        var verifier = new PackageVerifier();
        await using var service = new UpdateService(
            httpClient,
            new UpdateDownloader(httpClient, verifier),
            verifier,
            settingsService,
            _paths,
            new SemanticVersion(0, 1, 0));

        await service.CheckNowAsync(CancellationToken.None);

        var reloaded = new SettingsService(_paths).Load();
        Assert.NotNull(reloaded.Updates.LastCheckedAt);
    }

    [Fact]
    public async Task Beta_канал_ищет_prerelease_до_стабильного_latest_манифеста()
    {
        const string releasesUrl = "https://example.com/releases";
        const string stableManifestUrl = "https://example.com/latest/update-manifest.json";
        const string betaManifestUrl = "https://example.com/beta.5/update-manifest.json";

        var settingsService = new SettingsService(_paths);
        var settings = settingsService.Load();
        settings.Updates.Channel = UpdateChannel.Beta;
        settings.Updates.AutomaticDownload = false;
        settings.Updates.ManifestUrl = stableManifestUrl;
        settings.Updates.ReleasesApiUrl = releasesUrl;
        settingsService.Save(settings);

        var handler = new RoutingHandler(new Dictionary<string, string>
        {
            [releasesUrl] = $$"""
                [
                  {
                    "tag_name": "v0.3.0-beta.5",
                    "html_url": "https://example.com/release/beta.5",
                    "draft": false,
                    "prerelease": true,
                    "assets": [
                      {
                        "name": "update-manifest.json",
                        "browser_download_url": "{{betaManifestUrl}}",
                        "size": 500
                      },
                      {
                        "name": "VoiceFlowWin-Setup-x64-0.3.0-beta.5.exe",
                        "browser_download_url": "https://example.com/beta.5/setup.exe",
                        "size": 123456
                      }
                    ]
                  }
                ]
                """,
            [betaManifestUrl] = """
                {
                  "version": "0.3.0-beta.5",
                  "installerUrl": "https://example.com/beta.5/setup.exe",
                  "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                  "size": 123456,
                  "releaseNotesUrl": "https://example.com/release/beta.5",
                  "unsigned": true
                }
                """,
            [stableManifestUrl] = """
                {
                  "version": "0.2.4",
                  "installerUrl": "https://example.com/stable/setup.exe",
                  "sha256": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                  "size": 123456
                }
                """,
        });

        using var httpClient = new HttpClient(handler);
        var verifier = new PackageVerifier();
        await using var service = new UpdateService(
            httpClient,
            new UpdateDownloader(httpClient, verifier),
            verifier,
            settingsService,
            _paths,
            SemanticVersion.Parse("0.3.0-beta.4"));

        var status = await service.CheckNowAsync(CancellationToken.None);

        Assert.Equal(UpdateState.UpdateAvailable, status.State);
        Assert.Equal(SemanticVersion.Parse("0.3.0-beta.5"), status.AvailableVersion);
        Assert.Contains(releasesUrl, handler.Requests);
        Assert.Contains(betaManifestUrl, handler.Requests);
        Assert.DoesNotContain(stableManifestUrl, handler.Requests);
    }

    private sealed class RoutingHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);

            return Task.FromResult(responses.TryGetValue(url, out var content)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}

public class PackageVerifierTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vfw-tests-" + Guid.NewGuid().ToString("N"));

    public PackageVerifierTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private (string Path, UpdateManifest Manifest) CreatePackage(string name = "VoiceFlowWin-Setup-x64-1.4.2.exe")
    {
        var path = Path.Combine(_directory, name);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(path, content);

        var manifest = new UpdateManifest
        {
            Version = "1.4.2",
            InstallerUrl = "https://example.com/" + name,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)),
            Size = content.Length,
            Unsigned = true,
        };

        return (path, manifest);
    }

    [Fact]
    public void Корректный_пакет_проходит_проверку()
    {
        var (path, manifest) = CreatePackage();

        Assert.True(new PackageVerifier().Verify(path, manifest, _directory).IsValid);
    }

    [Fact]
    public void Несовпадение_контрольной_суммы_отклоняет_пакет()
    {
        var (path, manifest) = CreatePackage();
        manifest.Sha256 = new string('B', 64);

        var result = new PackageVerifier().Verify(path, manifest, _directory);

        Assert.False(result.IsValid);
        Assert.Contains("SHA-256", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Несовпадение_размера_отклоняет_пакет()
    {
        var (path, manifest) = CreatePackage();
        manifest.Size = 999;

        Assert.False(new PackageVerifier().Verify(path, manifest, _directory).IsValid);
    }

    [Fact]
    public void Файл_вне_каталога_обновлений_отклоняется()
    {
        var (_, manifest) = CreatePackage();
        var outsidePath = Path.Combine(Path.GetTempPath(), "чужой.exe");
        File.WriteAllBytes(outsidePath, new byte[10]);

        try
        {
            Assert.False(new PackageVerifier().Verify(outsidePath, manifest, _directory).IsValid);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Theory]
    [InlineData("https://example.com/setup.exe", true, "setup.exe")]
    [InlineData("https://example.com/dir/VoiceFlowWin-Portable.zip", true, "VoiceFlowWin-Portable.zip")]
    [InlineData("http://example.com/setup.exe", false, "")]
    [InlineData("https://example.com/setup.bat", false, "")]
    // Закодированный обход каталога срезается до простого имени файла.
    [InlineData("https://example.com/..%2F..%2Fsetup.exe", true, "setup.exe")]
    public void Имя_файла_из_ссылки_проверяется(string url, bool expected, string expectedName)
    {
        var ok = PackageVerifier.TryGetSafeFileName(url, out var fileName);

        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.Equal(expectedName, fileName);
        }
    }

    [Fact]
    public void Обход_каталога_не_проходит()
    {
        var traversal = Path.Combine(_directory, "..", "..", "windows", "system32", "evil.exe");

        Assert.False(PackageVerifier.IsInsideDirectory(traversal, _directory));
        Assert.True(PackageVerifier.IsInsideDirectory(Path.Combine(_directory, "setup.exe"), _directory));
    }
}
