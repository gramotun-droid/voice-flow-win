using System.Globalization;

namespace VoiceFlowWin.Updater;

/// <summary>
/// Версия по правилам Semantic Versioning.
/// </summary>
/// <remarks>
/// Сравнение версий — это то место, где ошибка приводит к бесконечному циклу
/// «обновись на самого себя» или к откату на старую сборку. Поэтому реализация
/// своя и покрыта тестами: предрелизные версии считаются младше релизных,
/// а разряды сравниваются числами, чтобы 1.10.0 оказалась старше 1.9.0.
/// </remarks>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemanticVersion>
{
    public static readonly SemanticVersion Zero = new(0, 0, 0);

    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // Метаданные сборки на порядок версий не влияют.
        var plusIndex = text.IndexOf('+');
        if (plusIndex >= 0)
        {
            text = text[..plusIndex];
        }

        string? preRelease = null;
        var dashIndex = text.IndexOf('-');
        if (dashIndex >= 0)
        {
            preRelease = text[(dashIndex + 1)..];
            text = text[..dashIndex];
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = 0;
        var patch = 0;

        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        if (parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, string.IsNullOrEmpty(preRelease) ? null : preRelease);
        return true;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version) ? version : throw new FormatException($"Некорректная версия: {value}");

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // Релиз старше любой своей предрелизной версии: 1.4.0 > 1.4.0-beta.1.
        if (IsPreRelease && !other.IsPreRelease)
        {
            return -1;
        }

        if (!IsPreRelease && other.IsPreRelease)
        {
            return 1;
        }

        return string.Compare(PreRelease, other.PreRelease, StringComparison.OrdinalIgnoreCase);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
