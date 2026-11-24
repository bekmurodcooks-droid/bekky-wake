using System.Globalization;
using InsomniaWin.Core.Core;
using InsomniaWin.Core.Models;
using Xunit;

namespace InsomniaWin.Tests;

public sealed class SessionMathTests
{
    [Fact]
    public void Clamp_EnforcesMinimumAndMaximumBounds()
    {
        Assert.Equal(60, SessionMath.Clamp(5, 3600));
        Assert.Equal(3600, SessionMath.Clamp(99999, 3600));
        Assert.Equal(1800, SessionMath.Clamp(1800, 3600));
    }

    [Fact]
    public void Extended_NeverExceedsNowPlusMaxDuration()
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var session = SessionMath.NewSession(now, 3000, 3600);
        var extended = SessionMath.Extended(session, 5000, now.AddMinutes(10), 3600);

        Assert.Equal(now.AddMinutes(10).AddSeconds(3600), extended.EndsAt);
        Assert.Single(extended.Extensions);
    }

    [Fact]
    public void FormatCountdown_RoundsUpPartialSecondsAndHoldsShape()
    {
        Assert.Equal("30:00", SessionMath.FormatCountdown(1799.2, CountdownShape.Minutes));
        Assert.Equal("04:07", SessionMath.FormatCountdown(246.1, CountdownShape.Minutes));
        Assert.Equal("0:04:07", SessionMath.FormatCountdown(246.1, CountdownShape.Hours));
        Assert.Equal("1d 0:00:01", SessionMath.FormatCountdown(86400.5, CountdownShape.Days));
        Assert.Equal("00:00", SessionMath.FormatCountdown(-5, CountdownShape.Minutes));
    }

    [Fact]
    public void FormatRemaining_DropsZeroSecondaryUnits()
    {
        Assert.Equal("<1m", SessionMath.FormatRemaining(45));
        Assert.Equal("14m", SessionMath.FormatRemaining(14 * 60 + 25));
        Assert.Equal("2h", SessionMath.FormatRemaining(2 * 3600));
        Assert.Equal("2h 14m", SessionMath.FormatRemaining(2 * 3600 + 14 * 60));
        Assert.Equal("3d 2h", SessionMath.FormatRemaining(3 * 86400 + 2 * 3600));
    }

    [Fact]
    public void FormatEndTime_AddsWeekdayOrDateWhenSpanningDays()
    {
        var ci = CultureInfo.InvariantCulture;
        var now = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal("14:30", SessionMath.FormatEndTime(now.AddHours(4.5), now, ci));
        Assert.Contains("Thu", SessionMath.FormatEndTime(now.AddDays(2), now, ci));
        Assert.Contains("Dec", SessionMath.FormatEndTime(now.AddDays(10), now, ci));
    }
}
