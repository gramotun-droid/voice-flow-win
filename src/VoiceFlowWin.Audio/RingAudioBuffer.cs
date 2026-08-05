using VoiceFlowWin.Core.Abstractions;

namespace VoiceFlowWin.Audio;

/// <summary>
/// Кольцевой буфер последних N миллисекунд звука.
/// </summary>
/// <remarks>
/// Нужен для pre-roll: VAD понимает, что началась речь, уже после первых
/// звуков слова, и без запаса «п» в слове «привет» просто не попало бы в
/// сегмент. Буфер пишется всегда, пока идёт захват, и имеет фиксированный
/// размер — память не растёт при длинной диктовке.
/// </remarks>
public sealed class RingAudioBuffer
{
    private readonly byte[] _buffer;
    private readonly object _sync = new();
    private int _writePosition;
    private int _length;

    public RingAudioBuffer(int capacityMilliseconds)
    {
        if (capacityMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityMilliseconds), capacityMilliseconds, "Ёмкость должна быть положительной.");
        }

        CapacityMilliseconds = capacityMilliseconds;
        _buffer = new byte[AudioFormat.BytesFromMilliseconds(capacityMilliseconds)];
    }

    public int CapacityMilliseconds { get; }

    public int CapacityBytes => _buffer.Length;

    /// <summary>Сколько байт сейчас хранится.</summary>
    public int Length
    {
        get
        {
            lock (_sync)
            {
                return _length;
            }
        }
    }

    public int LengthMilliseconds => AudioFormat.MillisecondsFromBytes(Length);

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            // Если пришло больше, чем вмещает буфер, старое всё равно потеряно —
            // берём только последний хвост.
            if (data.Length >= _buffer.Length)
            {
                data[^_buffer.Length..].CopyTo(_buffer);
                _writePosition = 0;
                _length = _buffer.Length;
                return;
            }

            var firstChunk = Math.Min(data.Length, _buffer.Length - _writePosition);
            data[..firstChunk].CopyTo(_buffer.AsSpan(_writePosition));

            var remaining = data.Length - firstChunk;
            if (remaining > 0)
            {
                data[firstChunk..].CopyTo(_buffer.AsSpan(0));
            }

            _writePosition = (_writePosition + data.Length) % _buffer.Length;
            _length = Math.Min(_buffer.Length, _length + data.Length);
        }
    }

    /// <summary>Возвращает последние <paramref name="milliseconds"/> мс звука в хронологическом порядке.</summary>
    public byte[] ReadLast(int milliseconds)
    {
        var requested = AudioFormat.BytesFromMilliseconds(milliseconds);

        lock (_sync)
        {
            var take = Math.Min(requested, _length);
            if (take <= 0)
            {
                return Array.Empty<byte>();
            }

            // Выравнивание по границе сэмпла: половина 16-битного отсчёта
            // превратилась бы в щелчок.
            take -= take % AudioFormat.BytesPerSample;

            var result = new byte[take];
            var start = (_writePosition - take + _buffer.Length) % _buffer.Length;
            var firstChunk = Math.Min(take, _buffer.Length - start);
            _buffer.AsSpan(start, firstChunk).CopyTo(result);

            if (take > firstChunk)
            {
                _buffer.AsSpan(0, take - firstChunk).CopyTo(result.AsSpan(firstChunk));
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            // Звук не должен оставаться в памяти дольше, чем нужен.
            Array.Clear(_buffer);
            _writePosition = 0;
            _length = 0;
        }
    }
}
