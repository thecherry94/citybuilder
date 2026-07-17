using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Persistence;

/// <summary>Snapshot-based undo/redo over the whole network, built on the
/// byte-stable SaveLoad round-trip (fuzz-certified) instead of invertible deltas —
/// inverting commit-with-splits/heals is exactly where bugs live, and quickload
/// already proves the restore path end to end. Call <see cref="Checkpoint"/> BEFORE
/// a mutation; it dedupes by <see cref="RoadNetwork.Version"/>, so optimistic
/// checkpoints ahead of operations that then fail never leave junk entries.
/// The caller owns post-restore side effects (traffic resync, tool reset) exactly
/// like quickload.</summary>
public sealed class UndoStack(RoadNetwork network, int capacity = 50)
{
    private readonly List<(int Version, string State)> _undo = new();
    private readonly List<string> _redo = new();

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    public void Checkpoint()
    {
        if (_undo.Count > 0 && _undo[^1].Version == network.Version)
            return; // nothing changed since the last checkpoint
        _undo.Add((network.Version, SaveLoad.Save(network)));
        _redo.Clear();
        if (_undo.Count > capacity)
            _undo.RemoveAt(0);
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        _redo.Add(SaveLoad.Save(network));
        var (_, state) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        SaveLoad.LoadInto(state, network);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        _undo.Add((network.Version, SaveLoad.Save(network)));
        var state = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        SaveLoad.LoadInto(state, network);
        if (_undo.Count > capacity)
            _undo.RemoveAt(0);
        return true;
    }
}
