using Xunit;

namespace VoiceFlowWin.Tests;

/// <summary>
/// Интеграционные проверки моделей включаются явным флагом в scheduled/manual CI,
/// а обычный быстрый прогон не скачивает сотни мегабайт.
/// </summary>
public sealed class ModelIntegrationFactAttribute : FactAttribute
{
    public ModelIntegrationFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VOICEFLOWWIN_RUN_MODEL_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Задайте VOICEFLOWWIN_RUN_MODEL_TESTS=1; тест скачивает настоящую модель.";
        }
    }
}
