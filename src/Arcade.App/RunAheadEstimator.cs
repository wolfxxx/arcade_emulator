namespace Arcade.App;

/// <summary>
/// Turns timings of how long a game took to answer the controls into a run-ahead setting. A game
/// answers some changes later than others (a turn may wait for an animation to finish), but never
/// sooner than its built-in delay, so the shortest timing is that delay. Running further ahead than
/// it would skip the first frames of each reaction, so this never goes above the shortest one seen.
/// </summary>
sealed class RunAheadEstimator(int? known)
{
    /// <summary>Timings needed before the first estimate, and after which timing stops.</summary>
    public const int SamplesNeeded = 3, SamplesWanted = 12;

    /// <summary>The most frames run ahead automatically (each costs a frame of work per frame).</summary>
    public const int MaxFrames = 3;

    readonly List<int> _samples = new();

    /// <summary>Frames to run ahead: what an earlier session measured until there are timings of our own.</summary>
    public int Frames { get; private set; } = known ?? 0;

    bool _gaveUp;

    public bool Done => _gaveUp || _samples.Count >= SamplesWanted;

    /// <summary>True once there is a value, measured now or before.</summary>
    public bool HasEstimate => known != null || _samples.Count >= SamplesNeeded;

    /// <summary>Stops timing (the game can't be timed); <see cref="Frames"/> keeps what it had.</summary>
    public void GiveUp() => _gaveUp = true;

    /// <summary>Adds a timing; returns true when that changed <see cref="Frames"/> from what was known.</summary>
    public bool Add(int lag)
    {
        if (Done)
            return false;
        _samples.Add(Math.Clamp(lag, 0, MaxFrames));
        if (_samples.Count < SamplesNeeded)
            return false;
        var frames = _samples.Min();
        var changed = frames != Frames || (known == null && _samples.Count == SamplesNeeded);
        Frames = frames;
        return changed;
    }
}
