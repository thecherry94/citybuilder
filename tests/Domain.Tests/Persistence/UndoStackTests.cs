using System.Numerics;
using CityBuilder.Domain.Network;
using CityBuilder.Domain.Persistence;
using CityBuilder.Domain.Tests.Network;
using Xunit;

namespace CityBuilder.Domain.Tests.Persistence;

public class UndoStackTests
{
    private static RoadNetwork Net1()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
        return n;
    }

    [Fact]
    public void UndoRestoresPreMutationState()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        string before = SaveLoad.Save(n);
        undo.Checkpoint();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(100, 0, 50)));
        Assert.Equal(2, n.Edges.Count);
        Assert.True(undo.Undo());
        Assert.Equal(before, SaveLoad.Save(n));
        Assert.Single(n.Edges);
    }

    [Fact]
    public void RedoReappliesUndoneState()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        undo.Checkpoint();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(100, 0, 50)));
        string after = SaveLoad.Save(n);
        undo.Undo();
        Assert.True(undo.Redo());
        Assert.Equal(after, SaveLoad.Save(n));
    }

    [Fact]
    public void CheckpointDedupesByVersion()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        undo.Checkpoint();
        undo.Checkpoint(); // nothing changed between the two — must not double-push
        Assert.Equal(1, undo.UndoCount);
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(100, 0, 50)));
        undo.Checkpoint();
        Assert.Equal(2, undo.UndoCount);
    }

    [Fact]
    public void NewCheckpointClearsRedo()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        undo.Checkpoint();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 50), new Vector3(100, 0, 50)));
        undo.Undo();
        Assert.Equal(1, undo.RedoCount);
        undo.Checkpoint();
        Net.Commit(n, Net.Straight(new Vector3(0, 0, 80), new Vector3(100, 0, 80)));
        Assert.Equal(0, undo.RedoCount);
    }

    [Fact]
    public void EmptyStacksReturnFalse()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        Assert.False(undo.Undo());
        Assert.False(undo.Redo());
    }

    [Fact]
    public void CapacityTrimsOldest()
    {
        var n = Net1();
        var undo = new UndoStack(n, capacity: 3);
        for (int i = 0; i < 5; i++)
        {
            undo.Checkpoint();
            Net.Commit(n, Net.Straight(new Vector3(0, 0, 20 + i * 15), new Vector3(100, 0, 20 + i * 15)));
        }
        Assert.Equal(3, undo.UndoCount);
        int undone = 0;
        while (undo.Undo()) undone++;
        Assert.Equal(3, undone);
        Assert.Equal(3, n.Edges.Count); // 1 + 5 commits − 3 undos
    }

    [Fact]
    public void UndoAllRedoAllIsByteExact()
    {
        var n = Net1();
        var undo = new UndoStack(n);
        var states = new List<string> { SaveLoad.Save(n) };
        for (int i = 0; i < 4; i++)
        {
            undo.Checkpoint();
            Net.Commit(n, Net.Straight(new Vector3(0, 0, 20 + i * 15), new Vector3(100, 0, 20 + i * 15)));
            states.Add(SaveLoad.Save(n));
        }
        for (int i = 3; i >= 0; i--)
        {
            Assert.True(undo.Undo());
            Assert.Equal(states[i], SaveLoad.Save(n));
        }
        for (int i = 1; i <= 4; i++)
        {
            Assert.True(undo.Redo());
            Assert.Equal(states[i], SaveLoad.Save(n));
        }
    }

    [Fact]
    public void PerfGuard480EdgeGrid()
    {
        // checkpoint + undo on the KPI-scale grid must stay editor-instant
        var n = Net.New();
        for (int j = 0; j < 16; j++)
        {
            Net.Commit(n, Net.Straight(new Vector3(0, 0, j * 100), new Vector3(1500, 0, j * 100)));
            Net.Commit(n, Net.Straight(new Vector3(j * 100, 0, 0), new Vector3(j * 100, 0, 1500)));
        }
        var undo = new UndoStack(n);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        undo.Checkpoint();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"checkpoint took {sw.ElapsedMilliseconds} ms");
        Net.Commit(n, Net.Straight(new Vector3(0, 0, -50), new Vector3(1500, 0, -50)));
        sw.Restart();
        Assert.True(undo.Undo());
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100, $"undo took {sw.ElapsedMilliseconds} ms");
    }
}
