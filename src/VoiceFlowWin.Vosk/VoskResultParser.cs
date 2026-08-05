using System.Text.Json;

namespace VoiceFlowWin.VoskEngine;

/// <summary>
/// Разбирает JSON, который отдаёт Vosk.
/// </summary>
/// <remarks>
/// Формат простой: <c>{"partial":"…"}</c> для промежуточных гипотез и
/// <c>{"text":"…"}</c> для финальных. Разбор вынесен отдельно, потому что это
/// единственная часть работы с Vosk, которую можно проверить тестом без
/// native-библиотеки и без модели.
/// </remarks>
public static class VoskResultParser
{
    public static string ParsePartial(string? json) => ExtractProperty(json, "partial");

    public static string ParseFinal(string? json) => ExtractProperty(json, "text");

    private static string ExtractProperty(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            // Битый ответ распознавателя не должен ронять диктовку.
            return string.Empty;
        }
    }
}
