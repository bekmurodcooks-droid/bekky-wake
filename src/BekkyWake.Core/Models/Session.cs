using System.Text.Json.Serialization;

namespace BekkyWake.Core.Models;

/// <summary>
/// Shape of the live countdown, chosen once from the session's initial span
/// so UI layout never jitters as units drop off.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CountdownShape>))]
public enum CountdownShape
{
    Minutes,
    Hours,
    Days
}

/// <summary>
/// An active keep-awake session. EndsAt is the authoritative deadline.
/// </summary>
public sealed record Session
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }

    /// <summary>
    /// Every extension applied, in order, in seconds.
    /// </summary>
    public List<double> Extensions { get; init; } = [];

    [JsonConstructor]
    public Session(DateTimeOffset startedAt, DateTimeOffset endsAt, List<double>? extensions = null)
    {
        StartedAt = startedAt;
        EndsAt = endsAt;
        Extensions = extensions ?? [];
    }

    public TimeSpan Remaining(DateTimeOffset now)
    {
        var diff = EndsAt - now;
        return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
    }

    public double RemainingSeconds(DateTimeOffset now) => Remaining(now).TotalSeconds;

    public bool IsExpired(DateTimeOffset now) => EndsAt <= now;

    [JsonIgnore]
    public CountdownShape CountdownShape
    {
        get
        {
            var spanSeconds = (EndsAt - StartedAt).TotalSeconds;
            if (spanSeconds >= 86400) return CountdownShape.Days;
            if (spanSeconds >= 3600) return CountdownShape.Hours;
            return CountdownShape.Minutes;
        }
    }
}
