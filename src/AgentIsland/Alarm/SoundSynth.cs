using System.IO;
using AgentIsland.Core;

namespace AgentIsland.Alarm;

/// Synthesizes the alarm sound palette. The macOS build leans on the
/// system's classic alert sounds (Basso, Blow, Bottle, Frog, …); those
/// files can't ship with a Windows port, so we generate short chimes with
/// the same character — one WAV per preset, written once under
/// %APPDATA%\AgentIsland\sounds\v2 and reused after that. The directory is
/// versioned: bumping it invalidates stale caches when timbres change.
public static class SoundSynth
{
    private const int SampleRate = 44_100;
    private const string CacheVersion = "v2";

    public static string? EnsurePreset(string key)
    {
        try
        {
            var dir = Path.Combine(IslandPaths.AppSupportDir, "sounds", CacheVersion);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, key + ".wav");
            if (File.Exists(path)) return path;
            var samples = key switch
            {
                "Basso" => Basso(),
                "Blow" => Blow(),
                "Bottle" => Bottle(),
                "Frog" => Frog(),
                "Funk" => Funk(),
                "Glass" => Glass(),
                "Hero" => Hero(),
                "Morse" => Morse(),
                "Ping" => Ping(),
                "Pop" => Pop(),
                "Purr" => Purr(),
                "Sosumi" => Sosumi(),
                "Submarine" => Submarine(),
                "Tink" => Tink(),
                _ => Glass(),
            };
            WriteWav(path, samples);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // MARK: - Palette
    // Swept-pitch voices accumulate phase per sample instead of computing
    // sin(2π·f(t)·t) — the naive form re-stretches all elapsed phase every
    // time f changes and reads as a chirpy artifact.

    /// Deep, soft thud — low fundamental with a quick downward settle and
    /// two dark partials.
    private static float[] Basso()
    {
        double phase = 0;
        return Render(0.75, t =>
        {
            var f = 92.0 * (1 - 0.12 * Math.Min(1, t * 7));
            phase += Tau(f) / SampleRate;
            var env = Math.Min(1, t * 90) * Math.Exp(-t * 5.5);
            return (Math.Sin(phase) * 0.8
                + Math.Sin(phase * 2.01) * 0.26 * Math.Exp(-t * 8)
                + Math.Sin(phase * 3.02) * 0.10 * Math.Exp(-t * 12)) * env;
        });
    }

    /// Breathy "pah" — filtered noise burst with a hollow pipe resonance.
    private static float[] Blow()
    {
        var random = new Random(3);
        double lowpass = 0;
        return Render(0.5, t =>
        {
            var cutoff = 0.55 - 0.45 * Math.Min(1, t * 3);
            var noise = random.NextDouble() * 2 - 1;
            lowpass += (noise - lowpass) * cutoff;
            var pipe = Math.Sin(Tau(940) * t) * 0.22 * Math.Exp(-t * 9);
            var env = Math.Min(1, t * 30) * Math.Exp(-t * 7);
            return (lowpass * 1.5 + pipe) * env;
        });
    }

    /// Hollow blown-bottle knock with a breath transient and a slight sag.
    private static float[] Bottle()
    {
        var random = new Random(5);
        double phase = 0;
        return Render(0.45, t =>
        {
            var f = 587.0 * (1 - 0.04 * Math.Min(1, t * 10));
            phase += Tau(f) / SampleRate;
            var env = Math.Min(1, t * 60) * Math.Exp(-t * 9);
            var breath = (random.NextDouble() * 2 - 1) * 0.10 * Math.Exp(-t * 16);
            return (Math.Sin(phase) * 0.8 + Math.Sin(phase * 2) * 0.08 + breath) * env;
        });
    }

    /// Croak: low gliding tone under a fast amplitude wobble.
    private static float[] Frog()
    {
        double phase = 0;
        return Render(0.42, t =>
        {
            var f = 193.0 * (1 - 0.06 * t);
            phase += Tau(f) / SampleRate;
            var env = Math.Min(1, t * 50) * Math.Exp(-t * 6.5);
            var wobble = 0.55 + 0.45 * Math.Sin(Tau(24) * t);
            var body = Math.Sin(phase) + 0.4 * Math.Sin(phase * 2) + 0.18 * Math.Sin(phase * 3);
            return body * wobble * env * 0.6;
        });
    }

    /// Short funky bass pluck — octave drop into a dark fundamental, upper
    /// harmonics damped fast like a palm-muted string.
    private static float[] Funk()
    {
        double phase = 0;
        return Render(0.4, t =>
        {
            var f = 174.0 * Math.Pow(0.5, Math.Min(1, t * 16));
            phase += Tau(f) / SampleRate;
            var env = Math.Min(1, t * 80) * Math.Exp(-t * 9);
            return (Math.Sin(phase) * 0.8
                + Math.Sin(phase * 2) * 0.35 * Math.Exp(-t * 18)
                + Math.Sin(phase * 3) * 0.12 * Math.Exp(-t * 26)) * env;
        });
    }

    /// Bright glass strike — beating partial pair over inharmonic overtones
    /// and a tiny noise chip at the onset; long shimmer.
    private static float[] Glass()
    {
        var random = new Random(7);
        return Render(1.4, t =>
        {
            var strike = Math.Exp(-t * 2.6);
            var chip = (random.NextDouble() * 2 - 1) * 0.25 * Math.Exp(-t * 260);
            return (Math.Sin(Tau(1568) * t) * 0.50
                + Math.Sin(Tau(1575) * t) * 0.24
                + Math.Sin(Tau(2793) * t) * 0.26 * Math.Exp(-t * 4)
                + Math.Sin(Tau(3520) * t) * 0.14 * Math.Exp(-t * 5.5)
                + Math.Sin(Tau(4709) * t) * 0.07 * Math.Exp(-t * 7)
                + chip) * strike;
        });
    }

    /// Rising major triad flourish, each note with body and a soft attack;
    /// the top note rings longest.
    private static float[] Hero()
    {
        return Render(1.0, t =>
        {
            double Note(double f, double start, double decay)
            {
                if (t < start) return 0;
                var dt = t - start;
                var env = Math.Min(1, dt * 120) * Math.Exp(-dt * decay);
                return (Math.Sin(Tau(f) * dt) * 0.7
                    + Math.Sin(Tau(f * 2) * dt) * 0.18
                    + Math.Sin(Tau(f * 3) * dt) * 0.05) * env;
            }
            return Note(523.25, 0.00, 4.6) * 0.5
                + Note(659.25, 0.12, 4.6) * 0.5
                + Note(783.99, 0.24, 3.2) * 0.62;
        });
    }

    /// Telegraph dits — four quick beeps on one pitch.
    private static float[] Morse()
    {
        return Render(0.55, t =>
        {
            double Dit(double start)
            {
                if (t < start) return 0;
                var dt = t - start;
                var env = Math.Min(1, dt * 300) * Math.Exp(-dt * 35);
                return (Math.Sin(Tau(988) * dt) * 0.85 + Math.Sin(Tau(1976) * dt) * 0.12) * env;
            }
            return Dit(0) + Dit(0.12) + Dit(0.24) + Dit(0.36);
        });
    }

    /// Single clean high ping with a slow beat in its tail.
    private static float[] Ping()
    {
        return Render(1.0, t =>
        {
            var env = Math.Exp(-t * 4);
            return (Math.Sin(Tau(1318.5) * t) * 0.58
                + Math.Sin(Tau(1324) * t) * 0.28
                + Math.Sin(Tau(2637) * t) * 0.14 * Math.Exp(-t * 6)
                + Math.Sin(Tau(3951) * t) * 0.05 * Math.Exp(-t * 8)) * env;
        });
    }

    /// Bubble pop — fast exponential pitch fall.
    private static float[] Pop()
    {
        double phase = 0;
        return Render(0.22, t =>
        {
            var f = 880.0 * Math.Exp(-t * 16);
            phase += Tau(f) / SampleRate;
            var env = Math.Min(1, t * 200) * Math.Exp(-t * 22);
            return Math.Sin(phase) * env;
        });
    }

    /// Two soft low purr bursts — slow AM over a dark fundamental.
    private static float[] Purr()
    {
        return Render(0.8, t =>
        {
            double Burst(double start, double length)
            {
                if (t < start || t > start + length) return 0;
                var dt = t - start;
                var env = Math.Min(1, dt * 40) * Math.Min(1, (start + length - t) * 25);
                var tremolo = 0.55 + 0.45 * Math.Sin(Tau(24) * dt);
                return (Math.Sin(Tau(76) * dt) + 0.4 * Math.Sin(Tau(152) * dt)) * tremolo * env;
            }
            return (Burst(0, 0.32) + Burst(0.44, 0.32)) * 0.9;
        });
    }

    /// Marimba-ish two-note blip — a grace note into the main strike with
    /// woody inharmonic partials.
    private static float[] Sosumi()
    {
        return Render(0.55, t =>
        {
            double Bar(double f, double start, double gain, double decay)
            {
                if (t < start) return 0;
                var dt = t - start;
                var env = Math.Min(1, dt * 250) * Math.Exp(-dt * decay);
                return (Math.Sin(Tau(f) * dt) * 0.7
                    + Math.Sin(Tau(f * 3.9) * dt) * 0.18 * Math.Exp(-dt * 12)
                    + Math.Sin(Tau(f * 9.2) * dt) * 0.05 * Math.Exp(-dt * 20)) * env * gain;
            }
            return Bar(880, 0, 0.4, 11) + Bar(1174.7, 0.07, 0.85, 6);
        });
    }

    /// Slow underwater warble — FM around a low carrier.
    private static float[] Submarine()
    {
        double phase = 0;
        return Render(1.1, t =>
        {
            var f = 290 + 20 * Math.Sin(Tau(4) * t);
            phase += Tau(f) / SampleRate;
            var env = Math.Min(1, t * 10) * Math.Exp(-t * 2.6);
            return (Math.Sin(phase) * 0.8 + Math.Sin(phase * 2) * 0.18) * env;
        });
    }

    /// Tiny high tink — short beating pair with a fast decay.
    private static float[] Tink()
    {
        return Render(0.25, t =>
        {
            var env = Math.Exp(-t * 13);
            return (Math.Sin(Tau(2093) * t) * 0.6
                + Math.Sin(Tau(2099) * t) * 0.3
                + Math.Sin(Tau(4186) * t) * 0.12 * Math.Exp(-t * 18)) * env;
        });
    }

    // MARK: - Engine

    private static double Tau(double frequency) => 2 * Math.PI * frequency;

    private static float[] Render(double seconds, Func<double, double> voice)
    {
        var count = (int)(seconds * SampleRate);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;
            samples[i] = (float)Math.Clamp(voice(t), -1, 1);
        }
        // 2ms attack ramp and a raised-cosine tail so neither edge clicks.
        var attack = Math.Min(count, SampleRate / 500);
        for (var i = 0; i < attack; i++)
        {
            samples[i] *= i / (float)attack;
        }
        var fade = Math.Min(count, SampleRate * 3 / 100);
        for (var i = 0; i < fade; i++)
        {
            var x = i / (double)fade;
            samples[count - 1 - i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * x));
        }
        return samples;
    }

    private static void WriteWav(string path, float[] samples)
    {
        // Peak-normalize so every preset lands at the same loudness; cap the
        // makeup gain so a quiet noise tail can't be blown up into hiss.
        float peak = 0;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        var gain = peak > 0 ? Math.Min(0.9f / peak, 2.5f) : 1f;

        // Write to a temp file and rename, so an interrupted write (crash /
        // power loss on first run) never leaves a truncated .wav that
        // EnsurePreset would then trust forever via File.Exists.
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            var dataLength = samples.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataLength);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);            // PCM
            writer.Write((short)1);            // mono
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);      // byte rate
            writer.Write((short)2);            // block align
            writer.Write((short)16);           // bits
            writer.Write("data"u8);
            writer.Write(dataLength);
            foreach (var sample in samples)
            {
                writer.Write((short)(Math.Clamp(sample * gain, -1, 1) * short.MaxValue));
            }
        }
        File.Move(tmp, path, overwrite: true);
    }
}
