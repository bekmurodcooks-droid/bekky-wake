using System.Globalization;
using InsomniaWin.Core.Models;

namespace InsomniaWin.Core.Core;

public static class SessionMath
{
    public const double MinimumDurationSeconds = 60;
    private const int WeekdayReachDays = 6;

    public static double Clamp(double durationSeconds, double maxDurationSeconds)
    {
        var upper = Math.Max(maxDurationSeconds, MinimumDurationSeconds);
        return Math.Min(Math.Max(durationSeconds, MinimumDurationSeconds), upper);
    }

    public static DateTimeOffset EndsAt(DateTimeOffset start, double durationSeconds, double maxDurationSeconds) =>
        start.AddSeconds(Clamp(durationSeconds, maxDurationSeconds));

    public static Session NewSession(DateTimeOffset now, double durationSeconds, double maxDurationSeconds) =>
        new(now, EndsAt(now, durationSeconds, maxDurationSeconds));

    public static Session Extended(Session session, double extraSeconds, DateTimeOffset now, double maxDurationSeconds)
    {
        var baseEnd = session.EndsAt > now ? session.EndsAt : now;
        var ceiling = now.AddSeconds(Math.Max(maxDurationSeconds, MinimumDurationSeconds));
        var proposed = baseEnd.AddSeconds(Math.Max(extraSeconds, 0));
        var newEnd = proposed < ceiling ? proposed : ceiling;

        var extensions = new List<double>(session.Extensions)
        {
            (newEnd - session.EndsAt).TotalSeconds
        };
        return new Session(session.StartedAt, newEnd, extensions);
    }

    public static double RemainingSeconds(DateTimeOffset endsAt, DateTimeOffset now) =>
        Math.Max((endsAt - now).TotalSeconds, 0);

    public static bool IsExpired(DateTimeOffset endsAt, DateTimeOffset now) =>
        endsAt <= now;

    public static string FormatRemaining(double intervalSeconds)
    {
        var totalMinutes = (long)Math.Floor(Math.Max(intervalSeconds, 0) / 60.0);
        if (totalMinutes < 1) return "<1m";

        var days = totalMinutes / (24 * 60);
        var hours = (totalMinutes % (24 * 60)) / 60;
        var minutes = totalMinutes % 60;

        if (days > 0)
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";

        if (hours > 0)
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";

        return $"{minutes}m";
    }

    public static string FormatCountdown(double remainingSeconds, CountdownShape shape)
    {
        var total = (long)Math.Ceiling(Math.Max(remainingSeconds, 0));
        var seconds = total % 60;
        var minutes = (total / 60) % 60;

        switch (shape)
        {
            case CountdownShape.Days:
                var days = total / 86400;
                if (days <= 0) return $"{total / 3600}:{minutes:00}:{seconds:00}";
                return $"{days}d {(total % 86400) / 3600}:{minutes:00}:{seconds:00}";
            case CountdownShape.Hours:
                return $"{total / 3600}:{minutes:00}:{seconds:00}";
            case CountdownShape.Minutes:
            default:
                return $"{total / 60:00}:{seconds:00}";
        }
    }

    public static string FormatEndTime(DateTimeOffset endsAt, DateTimeOffset now, CultureInfo? culture = null)
    {
        var ci = culture ?? CultureInfo.CurrentCulture;
        var localNow = now;
        var localEnd = endsAt.ToOffset(now.Offset);
        var dayDiff = (localEnd.Date - localNow.Date).Days;

        if (dayDiff < 1)
        {
            return localEnd.ToString("t", ci);
        }

        if (dayDiff <= WeekdayReachDays)
        {
            return $"{localEnd.ToString("ddd", ci)} {localEnd.ToString("t", ci)}";
        }

        return $"{localEnd.ToString("d MMM", ci)} {localEnd.ToString("t", ci)}";
    }

    public static string CountdownText(
        DateTimeOffset endsAt,
        CountdownShape shape,
        CountdownStyle style,
        DateTimeOffset now,
        CultureInfo? culture = null) =>
        style switch
        {
            CountdownStyle.Remaining => FormatCountdown(RemainingSeconds(endsAt, now), shape),
            CountdownStyle.EndTime => FormatEndTime(endsAt, now, culture),
            _ => FormatCountdown(RemainingSeconds(endsAt, now), shape)
        };

    public static string FormatPresetLabel(double seconds)
    {
        if (seconds >= 86400 && Math.Abs(seconds % 86400) < 0.001 && seconds > 86400)
        {
            return $"{(int)(seconds / 86400)}d";
        }

        if (seconds >= 3600 && Math.Abs(seconds % 3600) < 0.001)
        {
            return $"{(int)(seconds / 3600)}h";
        }

        var mins = Math.Max(1, (int)Math.Round(seconds / 60.0));
        return $"{mins}m";
    }
}
