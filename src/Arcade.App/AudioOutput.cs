using Arcade.Libretro;
using SDL;
using static SDL.SDL3;

namespace Arcade.App;

/// <summary>
/// Plays the core's audio through an SDL audio stream (which resamples to the device rate) and
/// applies dynamic rate control: the playback speed is nudged by up to ±0.5% to keep the queue
/// near its target. That absorbs the small mismatch between the game's timing and the real
/// display/audio clocks without audible pitch change, crackles, or growing latency.
/// </summary>
sealed unsafe class AudioOutput : IAudioSink, IDisposable
{
    const double TargetLatency = 0.050;   // seconds of audio we aim to keep queued
    const double MaxLatency = 0.250;      // beyond this (e.g. after a stall) drop the backlog
    const double MaxRateAdjust = 0.005;   // ±0.5%: below what ears notice as a pitch change

    SDL_AudioStream* _stream;
    int _sampleRate;
    double _smoothedQueue = TargetLatency;
    bool _started;

    /// <summary>Lowest queue level seen since the last <see cref="ResetStats"/>; near 0 means sound was about to gap.</summary>
    public double LowestQueuedSeconds { get; private set; } = double.MaxValue;
    /// <summary>Seconds until the queued audio has played (at the current <see cref="Speed"/>).</summary>
    public double QueuedSeconds => _stream == null ? 0 : SDL_GetAudioStreamQueued(_stream) / (4.0 * _sampleRate * _speed);
    public float CurrentRatio { get; private set; } = 1f;

    /// <summary>(Re)opens the output for the core's sample rate. Samples written before this are dropped.</summary>
    public void Configure(double sampleRate)
    {
        var rate = (int)Math.Round(sampleRate);
        if (rate == _sampleRate && _stream != null)
            return;
        Close();
        _sampleRate = rate;
        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_S16LE, channels = 2, freq = rate };
        _stream = SDL_OpenAudioDeviceStream((SDL_AudioDeviceID)SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, 0);
        if (_stream == null)
            Console.Error.WriteLine("Audio unavailable: " + SDL_GetError());
        _started = false;
    }

    /// <summary>Drops the core's audio instead of playing it (attract mode).</summary>
    public bool Muted { get; set; }

    /// <summary>Drops audio for a moment, e.g. while rewinding (separate from <see cref="Muted"/>, which the app owns).</summary>
    public bool Suppressed { get; set; }

    double _speed = 1;

    /// <summary>
    /// Game speed (fast-forward, slow motion). Sound plays faster or slower to match, like a tape;
    /// queued sound is dropped on a change so the delay stays short.
    /// </summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (value == _speed || value <= 0)
                return;
            _speed = value;
            Flush();
        }
    }

    public void Write(ReadOnlySpan<short> interleavedStereo)
    {
        if (_stream == null || interleavedStereo.IsEmpty || Muted || Suppressed)
            return;
        fixed (short* p = interleavedStereo)
            SDL_PutAudioStreamData(_stream, (nint)p, interleavedStereo.Length * sizeof(short));
        // Device streams open paused; start once the target latency is buffered so playback
        // begins with headroom instead of running near-empty until rate control catches up.
        if (!_started && QueuedSeconds >= TargetLatency)
        {
            SDL_ResumeAudioStreamDevice(_stream);
            _started = true;
        }
    }

    /// <summary>Call once per emulated frame, after the core has produced that frame's audio.</summary>
    public void UpdateRate()
    {
        if (_stream == null)
            return;
        var queued = QueuedSeconds;
        if (queued > MaxLatency)
        {
            SDL_ClearAudioStream(_stream);
            queued = 0;
        }
        LowestQueuedSeconds = Math.Min(LowestQueuedSeconds, queued);

        // Low-pass the measurement so the ratio doesn't jitter frame to frame.
        _smoothedQueue += (queued - _smoothedQueue) * 0.05;
        var deviation = Math.Clamp((_smoothedQueue - TargetLatency) / TargetLatency, -1, 1);
        // More queued than wanted -> consume slightly faster (ratio > 1), and vice versa.
        CurrentRatio = (float)(_speed * (1 + MaxRateAdjust * deviation));
        SDL_SetAudioStreamFrequencyRatio(_stream, CurrentRatio);
    }

    public void ResetStats() => LowestQueuedSeconds = double.MaxValue;

    /// <summary>Drops queued audio, e.g. when loading a save state so old sound doesn't play on.</summary>
    public void Flush()
    {
        if (_stream != null)
            SDL_ClearAudioStream(_stream);
    }

    /// <summary>Stops playback and drops queued audio, e.g. while a menu is open; it restarts by itself once enough new audio is queued.</summary>
    public void Pause()
    {
        if (_stream == null)
            return;
        SDL_PauseAudioStreamDevice(_stream);
        SDL_ClearAudioStream(_stream);
        _started = false;
    }

    /// <summary>Closes the device when a game ends; the next <see cref="Configure"/> reopens it.</summary>
    public void Stop()
    {
        Close();
        _sampleRate = 0;
        Muted = false;
        Suppressed = false;
        _speed = 1;
    }

    void Close()
    {
        if (_stream != null)
        {
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }
    }

    public void Dispose() => Close();
}
