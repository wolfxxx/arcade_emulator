using Arcade.App;

namespace Arcade.Tests;

public class RunAheadEstimatorTests
{
    [Fact]
    public void Uses_the_shortest_timing_once_there_are_enough()
    {
        var e = new RunAheadEstimator(known: null);
        Assert.Equal(0, e.Frames);
        Assert.False(e.Add(2));
        Assert.False(e.Add(3));
        Assert.True(e.Add(2));  // first estimate is always reported, so it can be kept
        Assert.Equal(2, e.Frames);
        Assert.False(e.Add(4)); // slower answers don't change it
        Assert.True(e.Add(1));
        Assert.Equal(1, e.Frames);
    }

    [Fact]
    public void Starts_from_an_earlier_measurement_and_stops_after_enough_timings()
    {
        var e = new RunAheadEstimator(known: 1);
        Assert.Equal(1, e.Frames);
        Assert.False(e.Add(1));
        Assert.False(e.Add(1));
        Assert.False(e.Add(1)); // same as known: nothing new to keep
        for (var i = 3; i < RunAheadEstimator.SamplesWanted; i++)
            e.Add(1);
        Assert.True(e.Done);
        Assert.False(e.Add(0));
        Assert.Equal(1, e.Frames);
    }

    [Fact]
    public void Giving_up_keeps_the_earlier_value_and_stops_timing()
    {
        var e = new RunAheadEstimator(known: null);
        Assert.False(e.HasEstimate);
        e.GiveUp();
        Assert.True(e.Done);
        Assert.Equal(0, e.Frames);
        Assert.True(new RunAheadEstimator(known: 2).HasEstimate);
    }

    [Fact]
    public void Never_runs_more_than_the_limit_ahead()
    {
        var e = new RunAheadEstimator(known: null);
        for (var i = 0; i < 3; i++)
            e.Add(7);
        Assert.Equal(RunAheadEstimator.MaxFrames, e.Frames);
    }
}
