// Generates the five M6.75 UI sounds as 16-bit mono 44.1 kHz WAVs. Deterministic,
// dependency-free — regenerate any time with:
//   dotnet run --project tools/sfxgen -- assets/audio
const int Rate = 44100;
string outDir = args.Length > 0 ? args[0] : "assets/audio";
Directory.CreateDirectory(outDir);

Write("tick.wav", Synth(0.045, t =>
    Math.Sin(2 * Math.PI * 1900 * t) * Env(t, attack: 0.002, tau: 0.010) * 0.5));
Write("click.wav", Synth(0.06, t =>
    (Math.Sin(2 * Math.PI * 950 * t) + 0.4 * Math.Sin(2 * Math.PI * 1400 * t))
    * Env(t, attack: 0.001, tau: 0.015) * 0.4));
Write("plop.wav", Synth(0.14, t =>
{
    double f = 380 - (380 - 160) * (t / 0.14); // downward sweep
    return Math.Sin(2 * Math.PI * f * t) * Env(t, attack: 0.004, tau: 0.05) * 0.55;
}));
Write("blip.wav", Synth(0.16, t =>
{
    double f = t < 0.07 ? 290 : 220; // two-tone descending error
    return Math.Tanh(2.2 * Math.Sin(2 * Math.PI * f * t)) * Env(t, attack: 0.002, tau: 0.06) * 0.35;
}));
var rng = new Random(675); // fixed seed: byte-stable output
double brown = 0;
Write("crunch.wav", Synth(0.22, t =>
{
    brown = 0.94 * brown + 0.35 * (rng.NextDouble() * 2 - 1); // integrated noise
    return brown * Env(t, attack: 0.003, tau: 0.07) * 0.8;
}));
Console.WriteLine($"sfxgen: wrote 5 wavs to {outDir}");
return;

static double Env(double t, double attack, double tau)
    => t < attack ? t / attack : Math.Exp(-(t - attack) / tau);

static short[] Synth(double seconds, Func<double, double> f)
{
    int n = (int)(seconds * Rate);
    var samples = new short[n];
    for (int i = 0; i < n; i++)
    {
        double v = Math.Clamp(f(i / (double)Rate), -1, 1);
        samples[i] = (short)(v * short.MaxValue);
    }
    return samples;
}

void Write(string name, short[] samples)
{
    using var fs = File.Create(Path.Combine(outDir, name));
    using var w = new BinaryWriter(fs);
    int dataLen = samples.Length * 2;
    w.Write("RIFF"u8);
    w.Write(36 + dataLen);
    w.Write("WAVE"u8);
    w.Write("fmt "u8);
    w.Write(16);
    w.Write((short)1);          // PCM
    w.Write((short)1);          // mono
    w.Write(Rate);
    w.Write(Rate * 2);          // byte rate
    w.Write((short)2);          // block align
    w.Write((short)16);         // bits
    w.Write("data"u8);
    w.Write(dataLen);
    foreach (var s in samples)
        w.Write(s);
}
