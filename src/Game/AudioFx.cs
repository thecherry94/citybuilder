using Godot;

namespace CityBuilder.Game;

public enum Sfx { SnapTick, Place, Commit, Reject, Bulldoze }

/// <summary>The project's sound effects: five one-shots on a small round-robin
/// player pool. Streams load from loose WAVs (AudioStreamWav.LoadFromFile) so the
/// import pipeline is not involved; snap ticks are rate-limited so guide-hopping
/// doesn't machine-gun. Headless runs get the dummy audio driver — Play is a
/// safe no-op there.</summary>
public partial class AudioFx : Node
{
    private readonly Dictionary<Sfx, AudioStream> _streams = new();
    private readonly Dictionary<Sfx, float> _volumeDb = new();
    private readonly List<AudioStreamPlayer> _players = new();
    private int _next;
    private ulong _lastTickMs;

    public int LoadedCount => _streams.Count;

    public override void _Ready()
    {
        foreach (var (sfx, file, db) in new (Sfx, string, float)[]
        {
            (Sfx.SnapTick, "tick.wav", -14f),
            (Sfx.Place, "click.wav", -8f),
            (Sfx.Commit, "plop.wav", -6f),
            (Sfx.Reject, "blip.wav", -9f),
            (Sfx.Bulldoze, "crunch.wav", -7f),
        })
        {
            var path = ProjectSettings.GlobalizePath($"res://assets/audio/{file}");
            if (AudioStreamWav.LoadFromFile(path) is { } stream)
            {
                _streams[sfx] = stream;
                _volumeDb[sfx] = db;
            }
            else
            {
                GD.PushWarning($"AudioFx: could not load {path}");
            }
        }
        for (int i = 0; i < 4; i++)
        {
            var p = new AudioStreamPlayer { Name = $"sfx{i}" };
            AddChild(p);
            _players.Add(p);
        }
    }

    public void Play(Sfx sfx)
    {
        if (sfx == Sfx.SnapTick)
        {
            ulong now = Time.GetTicksMsec();
            if (now - _lastTickMs < 60)
                return;
            _lastTickMs = now;
        }
        if (!_streams.TryGetValue(sfx, out var stream))
            return;
        var p = _players[_next];
        _next = (_next + 1) % _players.Count;
        p.Stream = stream;
        p.VolumeDb = _volumeDb[sfx];
        p.Play();
    }
}
