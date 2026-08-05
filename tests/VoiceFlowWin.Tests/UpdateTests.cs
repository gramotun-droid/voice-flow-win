using System.Security.Cryptography;
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
    public void Манифест_без_контрольной_суммы_отвергается()
    {
        var manifest = Valid();
        manifest.Sha256 = "коротко";

        Assert.False(manifest.IsValid(out _));
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
