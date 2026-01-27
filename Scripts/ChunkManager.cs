using Godot;
using NeinCraft.Scene.Chunk;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public partial class ChunkManager : Node
{
	public static ChunkManager Instance { get; private set; }

	[ExportGroup("Setup")]
	[Export] public Node3D Player { get; set; }
	[Export] public PackedScene ChunkScene { get; set; }

	[Export(PropertyHint.Range, "1,16,1")]
	public int ViewDistanceInChunks = 4;

	[Export] public Vector3I ChunkDimensions = new(16, 65, 16);
	[Export] public bool GenerateOnReady = true;

	[ExportGroup("Smooth Transitions")]
	[Export] public bool SmoothTransitions = false;
	[Export(PropertyHint.Range, "0.05,1.0,0.05")]
	public float ChunkAppearTime = 0.15f;
	[Export(PropertyHint.Range, "0.05,1.0,0.05")]
	public float ChunkDisappearTime = 0.1f;

	[ExportGroup("Performance")]
	[Export(PropertyHint.Range, "1,8,1")]
	public int MaxChunkCreatesPerFrame = 3;
	
	[Export(PropertyHint.Range, "1,16,1")]
	public int MaxChunkDestroyPerFrame = 8;
	
	[Export] public bool UseChunkPooling = true;
	
	[Export(PropertyHint.Range, "0,10,1")]
	public int UpdateSkipFrames = 2;

	[ExportGroup("Advanced Optimizations")]
	[Export] public bool UseCircularLoading = true;
	
	[Export(PropertyHint.Range, "1,4,1")]
	public int CollisionDistance = 2;
	
	[Export] public bool ForceCollisionOnAll = false;
	
	[Export] public bool UseSpatialHashing = true;

	// Spatial hash for O(1) lookups
	private readonly Dictionary<long, Chunk> _spatialHash = new();
	
	// Fallback
	private readonly Dictionary<Vector2I, Chunk> _positionToChunk = new();
	private readonly Dictionary<Chunk, Vector2I> _chunkToPosition = new();

	private readonly Queue<Vector2I> _createQueue = new();
	private readonly HashSet<Vector2I> _queuedCoords = new();
	private readonly Queue<Chunk> _destroyQueue = new();

	private readonly List<Vector2I> _toCreateSorted = new(512);
	private readonly List<Vector2I> _toRemove = new(512);
	
	private readonly Stack<Chunk> _chunkPool = new();
	private const int MaxPoolSize = 64;

	private Vector2I _centerChunk;
	private int _frameCounter = 0;
	
	private int _viewDistSq;
	private int _collisionDistSq;

	// CRITICAL FIX: Track initialization state
	private bool _isInitialized = false;

	public override void _Ready()
	{
		// CRITICAL FIX: Singleton pattern with validation
		if (Instance != null && Instance != this)
		{
			GD.PushError($"Multiple ChunkManager instances detected! Destroying duplicate.");
			QueueFree();
			return;
		}
		Instance = this;

		// CRITICAL FIX: Validate required exports
		if (Player == null)
		{
			GD.PushError("CRITICAL: ChunkManager.Player is not set!");
			return;
		}

		if (ChunkScene == null)
		{
			GD.PushError("CRITICAL: ChunkManager.ChunkScene is not set!");
			return;
		}

		// CRITICAL FIX: Validate chunk dimensions
		if (ChunkDimensions.X <= 0 || ChunkDimensions.Y <= 0 || ChunkDimensions.Z <= 0)
		{
			GD.PushError($"CRITICAL: Invalid ChunkDimensions: {ChunkDimensions}");
			ChunkDimensions = new Vector3I(16, 65, 16);
		}

		_viewDistSq = ViewDistanceInChunks * ViewDistanceInChunks;
		_collisionDistSq = CollisionDistance * CollisionDistance;

		// CRITICAL FIX: Initialize block mapping BEFORE any chunks are created
		var bm = BlockManager.Instance;
		if (bm == null)
		{
			GD.PushError("CRITICAL: BlockManager.Instance is null! Ensure BlockManager exists in scene.");
			return;
		}

		if (bm.Air == null || bm.Stone == null || bm.Dirt == null || bm.Grass == null)
		{
			GD.PushError("CRITICAL: BlockManager has null block references!");
			return;
		}

		Chunk.InitializeBlockMapping(bm.Air, bm.Stone, bm.Dirt, bm.Grass);

		_centerChunk = WorldToChunk(Player.GlobalPosition);
		_isInitialized = true;

		if (GenerateOnReady)
		{
			UpdateLoadedChunks();
		}

		GD.Print($"✓ ChunkManager initialized: View={ViewDistanceInChunks}, Collision={CollisionDistance}, Center={_centerChunk}");
	}

	public override void _Process(double delta)
	{
		// CRITICAL FIX: Validate initialization
		if (!_isInitialized || Player == null || ChunkScene == null)
			return;

		// Process queues first
		ProcessChunkCreateQueue();
		ProcessChunkDestroyQueue();

		// Skip position updates
		_frameCounter++;
		if (_frameCounter <= UpdateSkipFrames)
			return;
		_frameCounter = 0;

		var newCenter = WorldToChunk(Player.GlobalPosition);
		if (newCenter != _centerChunk)
		{
			_centerChunk = newCenter;
			UpdateLoadedChunks();
			UpdateChunkCollisions();
		}
	}

private void UpdateLoadedChunks()
	{
		_toCreateSorted.Clear();

		if (UseCircularLoading)
		{
			// OPTIMIZATION: Circular loading - skip corners
			for (int dz = -ViewDistanceInChunks; dz <= ViewDistanceInChunks; dz++)
			{
				for (int dx = -ViewDistanceInChunks; dx <= ViewDistanceInChunks; dx++)
				{
					int distSq = dx * dx + dz * dz;
					if (distSq > _viewDistSq + ViewDistanceInChunks)
						continue;

					var coord = new Vector2I(_centerChunk.X + dx, _centerChunk.Y + dz);

					if (ChunkExists(coord) || _queuedCoords.Contains(coord))
						continue;

					_toCreateSorted.Add(coord);
				}
			}
		}
		else
		{
			// Square loading
			for (int dz = -ViewDistanceInChunks; dz <= ViewDistanceInChunks; dz++)
			{
				for (int dx = -ViewDistanceInChunks; dx <= ViewDistanceInChunks; dx++)
				{
					var coord = new Vector2I(_centerChunk.X + dx, _centerChunk.Y + dz);

					if (ChunkExists(coord) || _queuedCoords.Contains(coord))
						continue;

					_toCreateSorted.Add(coord);
				}
			}
		}

		// OPTIMIZATION: Sort by distance for better loading priority
		_toCreateSorted.Sort((a, b) => DistanceSq(a, _centerChunk).CompareTo(DistanceSq(b, _centerChunk)));

		foreach (var coord in _toCreateSorted)
			EnqueueChunkCreate(coord);

		// Unload distant chunks
		_toRemove.Clear();

		if (UseSpatialHashing)
		{
			foreach (var kv in _spatialHash)
			{
				// Key contains the coord; don't depend on chunk->coord map to decide unloading.
				var coord = UnpackCoord(kv.Key);

				if (Mathf.Abs(coord.X - _centerChunk.X) > ViewDistanceInChunks ||
					Mathf.Abs(coord.Y - _centerChunk.Y) > ViewDistanceInChunks)
				{
					_toRemove.Add(coord);
				}
			}
		}
		else
		{
			foreach (var kv in _positionToChunk)
			{
				var coord = kv.Key;
				if (Mathf.Abs(coord.X - _centerChunk.X) > ViewDistanceInChunks ||
					Mathf.Abs(coord.Y - _centerChunk.Y) > ViewDistanceInChunks)
				{
					_toRemove.Add(coord);
				}
			}
		}

		foreach (var coord in _toRemove)
		{
			if (!TryGetChunk(coord, out var chunk))
				continue;

			RemoveChunk(coord, chunk);
			_destroyQueue.Enqueue(chunk);
		}
	}

	private void UpdateChunkCollisions()
	{
		if (UseSpatialHashing)
		{
			foreach (var kv in _spatialHash)
			{
				var chunk = kv.Value;
				if (chunk == null || !IsInstanceValid(chunk)) continue;
				
				var coord = GetChunkCoord(chunk);
				int dsq = DistanceSq(coord, _centerChunk);
				bool shouldHaveCollision = ForceCollisionOnAll || (dsq <= _collisionDistSq);
				
				if (chunk.BuildCollision != shouldHaveCollision)
				{
					chunk.BuildCollision = shouldHaveCollision;
					chunk.Rebuild();
				}
			}
		}
		else
		{
			foreach (var kv in _positionToChunk)
			{
				var chunk = kv.Value;
				if (chunk == null || !IsInstanceValid(chunk)) continue;
				
				var coord = kv.Key;
				int dsq = DistanceSq(coord, _centerChunk);
				bool shouldHaveCollision = ForceCollisionOnAll || (dsq <= _collisionDistSq);
				
				if (chunk.BuildCollision != shouldHaveCollision)
				{
					chunk.BuildCollision = shouldHaveCollision;
					chunk.Rebuild();
				}
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int DistanceSq(Vector2I a, Vector2I b)
	{
		int dx = a.X - b.X;
		int dz = a.Y - b.Y;
		return dx * dx + dz * dz;
	}

	// FIX: collision-free spatial key
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static long PackCoord(Vector2I coord)
	{
		// (X in high 32 bits) | (Z in low 32 bits)
		unchecked
		{
			return ((long)coord.X << 32) | (uint)coord.Y;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static Vector2I UnpackCoord(long key)
	{
		unchecked
		{
			int x = (int)(key >> 32);
			int z = (int)key; // low 32 bits
			return new Vector2I(x, z);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool ChunkExists(Vector2I coord)
	{
		if (UseSpatialHashing)
			return _spatialHash.ContainsKey(PackCoord(coord));
		return _positionToChunk.ContainsKey(coord);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryGetChunk(Vector2I coord, out Chunk chunk)
	{
		if (UseSpatialHashing)
			return _spatialHash.TryGetValue(PackCoord(coord), out chunk);
		return _positionToChunk.TryGetValue(coord, out chunk);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void AddChunk(Vector2I coord, Chunk chunk)
	{
		// NOTE: Should never overwrite because ChunkExists() guards it,
		// but if something goes wrong we clean old mappings to avoid "ghost" chunks.
		if (UseSpatialHashing)
		{
			long key = PackCoord(coord);
			if (_spatialHash.TryGetValue(key, out var existing) && existing != null && existing != chunk)
			{
				_chunkToPosition.Remove(existing);
			}

			_spatialHash[key] = chunk;
			_chunkToPosition[chunk] = coord;
		}
		else
		{
			_positionToChunk[coord] = chunk;
			_chunkToPosition[chunk] = coord;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void RemoveChunk(Vector2I coord, Chunk chunk)
	{
		if (UseSpatialHashing)
		{
			_spatialHash.Remove(PackCoord(coord));
			_chunkToPosition.Remove(chunk);
		}
		else
		{
			_positionToChunk.Remove(coord);
			_chunkToPosition.Remove(chunk);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private Vector2I GetChunkCoord(Chunk chunk)
	{
		return _chunkToPosition.TryGetValue(chunk, out var coord) ? coord : Vector2I.Zero;
	}

	private void EnqueueChunkCreate(Vector2I coord)
	{
		if (ChunkExists(coord) || _queuedCoords.Contains(coord))
			return;

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

			if (ChunkExists(coord))
				continue;

			CreateChunk(coord);
			toCreate--;
		}
	}

	private void ProcessChunkDestroyQueue()
	{
		int toDestroy = MaxChunkDestroyPerFrame;
		while (toDestroy > 0 && _destroyQueue.Count > 0)
		{
			var chunk = _destroyQueue.Dequeue();
			DespawnChunk(chunk);
			toDestroy--;
		}
	}

	private void CreateChunk(Vector2I coord)
	{
		Chunk chunk;
		bool isPooled = false;

		if (UseChunkPooling && _chunkPool.Count > 0)
		{
			chunk = _chunkPool.Pop();
			isPooled = true;
			chunk.Visible = true;
		}
		else
		{
			chunk = ChunkScene.Instantiate<Chunk>();
		}

		chunk.Dimensions = ChunkDimensions;

		// OPTIMIZATION: Only build collision for nearby chunks
		int dsq = DistanceSq(coord, _centerChunk);
		chunk.BuildCollision = dsq <= _collisionDistSq;

		// Set position before adding to tree
		var origin = new Vector3(coord.X * ChunkDimensions.X, 0, coord.Y * ChunkDimensions.Z);
		chunk.GlobalPosition = origin;

		if (SmoothTransitions)
			chunk.Scale = Vector3.Zero;

		// Add to tree
		if (!chunk.IsInsideTree())
			AddChild(chunk);

		AddChunk(coord, chunk);

		// For pooled chunks: regenerate content
		if (isPooled)
		{
			chunk.UpdateChunkPosition(coord);
			chunk.GenerateBlocks();
			chunk.Rebuild();
		}

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
			RecycleOrFreeChunk(chunk);
			return;
		}

		var tween = GetTree().CreateTween();
		tween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
		tween.TweenProperty(chunk, "scale", Vector3.Zero, ChunkDisappearTime);
		tween.TweenCallback(Callable.From(() =>
		{
			if (IsInstanceValid(chunk))
				RecycleOrFreeChunk(chunk);
		}));
	}

	private void RecycleOrFreeChunk(Chunk chunk)
	{
		if (UseChunkPooling && _chunkPool.Count < MaxPoolSize)
		{
			chunk.Visible = false;
			chunk.Scale = Vector3.One;
			_chunkPool.Push(chunk);
		}
		else
		{
			chunk.QueueFree();
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Vector2I WorldToChunk(Vector3 worldPos)
	{
		int cx = Mathf.FloorToInt(worldPos.X / (float)ChunkDimensions.X);
		int cz = Mathf.FloorToInt(worldPos.Z / (float)ChunkDimensions.Z);
		return new Vector2I(cx, cz);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
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
		return TryGetChunk(coord, out chunk);
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
