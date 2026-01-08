using Godot;
using NeinCraft.Scene.Chunk;
using System.Collections.Generic;

public partial class ChunkManager : Node
{
    public static ChunkManager Instance { get; private set; }

    [ExportGroup("Setup")]
    [Export] public Node3D Player { get; set; }
    [Export] public PackedScene ChunkScene { get; set; }

    [Export(PropertyHint.Range, "1,24,1")]
    public int ViewDistanceInChunks = 4;

    [Export] public Vector3I ChunkDimensions = new(16, 65, 16);
    [Export] public bool GenerateOnReady = true;

    [ExportGroup("Smooth Transitions")]
    [Export] public bool SmoothTransitions = true;
    [Export(PropertyHint.Range, "0.05,1.0,0.05")]
    public float ChunkAppearTime = 0.25f;
    [Export(PropertyHint.Range, "0.05,1.0,0.05")]
    public float ChunkDisappearTime = 0.25f;

    [ExportGroup("Performance")]
    [Export(PropertyHint.Range, "1,16,1")]
    public int MaxChunkCreatesPerFrame = 2;

    // loaded
    private readonly Dictionary<Vector2I, Chunk> _positionToChunk = new();
    private readonly Dictionary<Chunk, Vector2I> _chunkToPosition = new();

    // create queue
    private readonly Queue<Vector2I> _createQueue = new();
    private readonly HashSet<Vector2I> _queuedCoords = new();

    // reuse scratch lists (no per-update allocations)
    private readonly List<Vector2I> _toCreateSorted = new(512);
    private readonly List<Vector2I> _toRemove = new(512);

    private Vector2I _centerChunk;

    private const int COLLISION_RADIUS = 2; // in chunks

    public override void _Ready()
    {
        if (Instance != null && Instance != this)
        {
            QueueFree();
            return;
        }
        Instance = this;

        _centerChunk = WorldToChunk(Player != null ? Player.GlobalPosition : Vector3.Zero);
        if (GenerateOnReady)
            UpdateLoadedChunks();
    }

    public override void _Process(double delta)
    {
        if (Player == null || ChunkScene == null)
            return;

        var newCenter = WorldToChunk(Player.GlobalPosition);
        if (newCenter != _centerChunk)
        {
            _centerChunk = newCenter;
            UpdateLoadedChunks();
        }

        ProcessChunkCreateQueue();
    }

    private void UpdateLoadedChunks()
    {
        _toCreateSorted.Clear();

        // create: scan square and enqueue missing
        for (int dz = -ViewDistanceInChunks; dz <= ViewDistanceInChunks; dz++)
        {
            for (int dx = -ViewDistanceInChunks; dx <= ViewDistanceInChunks; dx++)
            {
                var coord = new Vector2I(_centerChunk.X + dx, _centerChunk.Y + dz);

                if (_positionToChunk.ContainsKey(coord)) continue;
                if (_queuedCoords.Contains(coord)) continue;

                _toCreateSorted.Add(coord);
            }
        }

        _toCreateSorted.Sort((a, b) => DistanceSq(a, _centerChunk).CompareTo(DistanceSq(b, _centerChunk)));
        for (int i = 0; i < _toCreateSorted.Count; i++)
            EnqueueChunkCreate(_toCreateSorted[i]);

        // unload: no HashSet needed, just distance check
        _toRemove.Clear();
        foreach (var kv in _positionToChunk)
        {
            var c = kv.Key;
            if (Mathf.Abs(c.X - _centerChunk.X) > ViewDistanceInChunks ||
                Mathf.Abs(c.Y - _centerChunk.Y) > ViewDistanceInChunks)
            {
                _toRemove.Add(c);
            }
        }

        for (int i = 0; i < _toRemove.Count; i++)
        {
            var coord = _toRemove[i];
            if (!_positionToChunk.TryGetValue(coord, out var chunk))
                continue;

            _positionToChunk.Remove(coord);
            _chunkToPosition.Remove(chunk);
            DespawnChunk(chunk);
        }
    }

    private static int DistanceSq(Vector2I a, Vector2I b)
    {
        int dx = a.X - b.X;
        int dz = a.Y - b.Y;
        return dx * dx + dz * dz;
    }

    private void EnqueueChunkCreate(Vector2I coord)
    {
        if (_positionToChunk.ContainsKey(coord)) return;
        if (_queuedCoords.Contains(coord)) return;

        _createQueue.Enqueue(coord);
        _queuedCoords.Add(coord);
    }

    private void ProcessChunkCreateQueue()
    {
        int toCreate = MaxChunkCreatesPerFrame;
        while (toCreate > 0 && _createQueue.Count > 0)
        {
            var coord = _createQueue.Dequeue();
            _queuedCoords.Remove(coord);

            if (_positionToChunk.ContainsKey(coord))
                continue;

            CreateChunk(coord);
            toCreate--;
        }
    }

    private void CreateChunk(Vector2I coord)
    {
        var chunk = ChunkScene.Instantiate<Chunk>();
        chunk.Dimensions = ChunkDimensions;

        // collision only near player to reduce physics cost
        int dsq = DistanceSq(coord, _centerChunk);
        chunk.BuildCollision = true;

        var origin = new Vector3(coord.X * ChunkDimensions.X, 0, coord.Y * ChunkDimensions.Z);
        chunk.GlobalPosition = origin;

        if (SmoothTransitions)
            chunk.Scale = Vector3.Zero;

        AddChild(chunk);
        _positionToChunk[coord] = chunk;
        _chunkToPosition[chunk] = coord;

        if (SmoothTransitions)
            SpawnChunk(chunk);
    }

    private void SpawnChunk(Chunk chunk)
    {
        if (!SmoothTransitions || !IsInstanceValid(chunk))
            return;

        var tween = GetTree().CreateTween();
        tween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(chunk, "scale", Vector3.One, ChunkAppearTime);
    }

    private void DespawnChunk(Chunk chunk)
    {
        if (!IsInstanceValid(chunk))
            return;

        if (!SmoothTransitions)
        {
            chunk.QueueFree();
            return;
        }

        var tween = GetTree().CreateTween();
        tween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tween.TweenProperty(chunk, "scale", Vector3.Zero, ChunkDisappearTime);
        tween.TweenCallback(Callable.From(() =>
        {
            if (IsInstanceValid(chunk))
                chunk.QueueFree();
        }));
    }

    public Vector2I WorldToChunk(Vector3 worldPos)
    {
        int cx = Mathf.FloorToInt(worldPos.X / (float)ChunkDimensions.X);
        int cz = Mathf.FloorToInt(worldPos.Z / (float)ChunkDimensions.Z);
        return new Vector2I(cx, cz);
    }

    public Vector3I WorldToLocalBlock(Vector3 worldPos, Vector2I chunkCoord)
    {
        var origin = new Vector3(chunkCoord.X * ChunkDimensions.X, 0, chunkCoord.Y * ChunkDimensions.Z);
        var local = worldPos - origin;

        return new Vector3I(
            Mathf.FloorToInt(local.X),
            Mathf.FloorToInt(local.Y),
            Mathf.FloorToInt(local.Z)
        );
    }

    public bool TryGetChunkAtWorld(Vector3 worldPos, out Chunk chunk, out Vector2I coord)
    {
        coord = WorldToChunk(worldPos);
        return _positionToChunk.TryGetValue(coord, out chunk);
    }

    public bool SetBlockAtWorld(Vector3 worldPos, Block block)
    {
        if (!TryGetChunkAtWorld(worldPos, out var chunk, out var coord))
            return false;

        var local = WorldToLocalBlock(worldPos, coord);
        if (!chunk.InBounds(local)) return false;

        var bm = BlockManager.Instance;
        if (block == null) block = bm.Air;

        if (!chunk.SetBlockLocal(local, block)) return false;
        chunk.Rebuild();
        return true;
    }

    public Block GetBlockAtWorld(Vector3 worldPos)
    {
        if (!TryGetChunkAtWorld(worldPos, out var chunk, out var coord))
            return null;

        var local = WorldToLocalBlock(worldPos, coord);
        return chunk.GetBlockLocal(local);
    }
}
