using System.Media;

namespace BekkyWake.UI;

/// <summary>
/// Generates gentle, crisp sine-chime WAV tones in memory when sessionSounds is enabled.
/// Loads nothing while sessionSounds is off.
/// </summary>
public static class SessionSounds
{
    public static void PlayStart() => PlayTone(frequencyHz: 587.33, durationMs: 120); // D5
    public static void PlayEnd() => PlayTone(frequencyHz: 440.00, durationMs: 140);   // A4

    private static void PlayTone(double frequencyHz, int durationMs)
    {
        _ = Task.Run(() =>
        {
            try
            {
                const int sampleRate = 22050;
                var samples = sampleRate * durationMs / 1000;
                using var ms = new MemoryStream(44 + samples * 2);
                using var bw = new BinaryWriter(ms);

                bw.Write("RIFF"u8);
                bw.Write(36 + samples * 2);
                bw.Write("WAVE"u8);
                bw.Write("fmt "u8);
                bw.Write(16);
                bw.Write((short)1); // PCM
                bw.Write((short)1); // Mono
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write("data"u8);
                bw.Write(samples * 2);

                for (var i = 0; i < samples; i++)
                {
                    var t = (double)i / sampleRate;
                    // Smooth cosine attack/decay envelope so there is zero click.
                    var env = Math.Sin(Math.PI * i / samples);
                    var val = Math.Sin(2.0 * Math.PI * frequencyHz * t) * env * 0.16;
                    bw.Write((short)(val * short.MaxValue));
                }

                ms.Position = 0;
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch
            {
                // Audio playback is strictly optional.
            }
        });
    }
}
