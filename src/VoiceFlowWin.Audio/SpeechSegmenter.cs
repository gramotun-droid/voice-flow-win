using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Core.Settings;

namespace VoiceFlowWin.Audio;

/// <summary>
/// Режет непрерывный поток микрофона на речевые сегменты.
/// </summary>
/// <remarks>
/// Сегмент открывается по срабатыванию VAD и обязательно включает pre-roll из
/// кольцевого буфера, иначе теряется начало первого слова. Закрывается он по
/// достаточной паузе, по максимальной длительности или по явной команде
/// пользователя. Слишком короткие всплески (щелчок мыши, стук по столу)
/// отбрасываются по <see cref="SegmentationSettings.MinSpeechMs"/>.
/// </remarks>
public sealed class SpeechSegmenter : ISpeechSegmenter
{
    private readonly IVoiceActivityDetector _detector;
    private readonly RingAudioBuffer _preRoll;
    private readonly List<byte> _currentSegment = new();
    private readonly List<byte> _frameAccumulator = new();
    private readonly object _sync = new();

    private SegmentationSettings _settings;
    private bool _inSpeech;
    private int _silenceMs;
    private int _speechMs;

    public SpeechSegmenter(IVoiceActivityDetector detector, SegmentationSettings settings)
    {
        _detector = detector;
        _settings = settings;
        _preRoll = new RingAudioBuffer(Math.Max(settings.PreRollMs, 100));
        _detector.Sensitivity = settings.VadSensitivity;
    }

    /// <summary>Речь началась: пора открывать сегмент в координаторе.</summary>
    public event EventHandler? SpeechStarted;

    /// <summary>Сегмент закрыт и готов для Whisper.</summary>
    public event EventHandler<SpeechSegmentEventArgs>? SegmentCompleted;

    /// <summary>Каждый обработанный кадр — для индикатора уровня в overlay.</summary>
    public event EventHandler<AudioLevelInfo>? LevelChanged;

    public bool IsInSpeech
    {
        get
        {
            lock (_sync)
            {
                return _inSpeech;
            }
        }
    }

    public int CurrentSegmentMilliseconds
    {
        get
        {
            lock (_sync)
            {
                return AudioFormat.MillisecondsFromBytes(_currentSegment.Count);
            }
        }
    }

    public void UpdateSettings(SegmentationSettings settings)
    {
        lock (_sync)
        {
            _settings = settings;
            _detector.Sensitivity = settings.VadSensitivity;
        }
    }

    /// <summary>Принимает очередной кусок звука произвольного размера.</summary>
    public void Push(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        var frameBytes = AudioFormat.BytesFromMilliseconds(_detector.FrameMilliseconds);

        lock (_sync)
        {
            _frameAccumulator.AddRange(pcm);

            while (_frameAccumulator.Count >= frameBytes)
            {
                var frame = _frameAccumulator.GetRange(0, frameBytes).ToArray();
                _frameAccumulator.RemoveRange(0, frameBytes);
                ProcessFrame(frame);
            }
        }
    }

    /// <summary>
    /// Принудительно закрывает сегмент: отпущена клавиша push-to-talk, повторно
    /// нажат toggle или нажат Esc.
    /// </summary>
    public void ForceComplete(SegmentEndReason reason)
    {
        SpeechSegmentEventArgs? completed = null;

        lock (_sync)
        {
            if (_frameAccumulator.Count > 0)
            {
                // Хвост, не набравший полный кадр, всё равно принадлежит фразе.
                if (_inSpeech)
                {
                    _currentSegment.AddRange(_frameAccumulator);
                }

                _frameAccumulator.Clear();
            }

            if (_inSpeech && _currentSegment.Count > 0)
            {
                completed = new SpeechSegmentEventArgs(_currentSegment.ToArray(), reason);
            }

            ResetSegmentState();
        }

        if (completed is not null)
        {
            SegmentCompleted?.Invoke(this, completed);
        }
    }

    /// <summary>Полный сброс между сеансами диктовки.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            ResetSegmentState();
            _frameAccumulator.Clear();
            _preRoll.Clear();
            _detector.Reset();
        }
    }

    private void ProcessFrame(byte[] frame)
    {
        var result = _detector.Process(frame);
        LevelChanged?.Invoke(this, new AudioLevelInfo(result.IsSpeech, result.Rms, AudioFormatConverter.ComputeLevelDb(frame)));

        if (!_inSpeech)
        {
            _preRoll.Write(frame);

            if (!result.IsSpeech)
            {
                return;
            }

            // Открываем сегмент, добавляя запас перед первым звуком.
            _inSpeech = true;
            _speechMs = 0;
            _silenceMs = 0;
            _currentSegment.Clear();
            _currentSegment.AddRange(_preRoll.ReadLast(_settings.PreRollMs));
            _preRoll.Clear();
            SpeechStarted?.Invoke(this, EventArgs.Empty);
        }

        _currentSegment.AddRange(frame);

        if (result.IsSpeech)
        {
            _speechMs += _detector.FrameMilliseconds;
            _silenceMs = 0;
        }
        else
        {
            _silenceMs += _detector.FrameMilliseconds;
        }

        if (_silenceMs >= _settings.SilenceToEndSegmentMs)
        {
            CompleteSegment(SegmentEndReason.Silence);
            return;
        }

        if (CurrentSegmentMillisecondsUnsafe() >= _settings.MaxSegmentSeconds * 1000)
        {
            CompleteSegment(SegmentEndReason.MaxDuration);
        }
    }

    private void CompleteSegment(SegmentEndReason reason)
    {
        var speechMs = _speechMs;
        var audio = TrimTrailingSilence(_currentSegment.ToArray(), reason);
        ResetSegmentState();

        // Короткие всплески — это не речь: щелчок мыши, кашель, хлопок двери.
        if (speechMs < _settings.MinSpeechMs || audio.Length == 0)
        {
            return;
        }

        SegmentCompleted?.Invoke(this, new SpeechSegmentEventArgs(audio, reason));
    }

    private byte[] TrimTrailingSilence(byte[] audio, SegmentEndReason reason)
    {
        if (reason != SegmentEndReason.Silence)
        {
            return audio;
        }

        // Из паузы, закрывшей сегмент, оставляем только post-roll: остальное
        // Whisper всё равно обработает как тишину, зря тратя время.
        var excessMs = _settings.SilenceToEndSegmentMs - _settings.PostRollMs;
        if (excessMs <= 0)
        {
            return audio;
        }

        var excessBytes = AudioFormat.BytesFromMilliseconds(excessMs);
        if (excessBytes >= audio.Length)
        {
            return audio;
        }

        return audio[..(audio.Length - excessBytes)];
    }

    private int CurrentSegmentMillisecondsUnsafe() => AudioFormat.MillisecondsFromBytes(_currentSegment.Count);

    private void ResetSegmentState()
    {
        _inSpeech = false;
        _silenceMs = 0;
        _speechMs = 0;
        _currentSegment.Clear();
    }
}
