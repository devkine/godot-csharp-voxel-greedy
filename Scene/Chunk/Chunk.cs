using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NeinCraft.Scene.Chunk
{
	[Tool]
	public partial class Chunk : StaticBody3D
	{
		[Export] public CollisionShape3D CollisionShape { get; set; }
		[Export] public MeshInstance3D MeshInstance { get; set; }
		[Export] public FastNoiseLite Noise { get; set; }

		[Export] public Vector3I Dimensions = new(16, 65, 16);

		[Export] public bool BuildCollision = true;
		[Export] public bool AsyncBuild = true;
		[Export(PropertyHint.Range, "0,2,1")] public int ShadowCasting = 1;

		// CRITICAL FIX: Use byte IDs with proper null safety
		private byte[] _blockIds;
		
		// CRITICAL FIX: Static lookup with thread-safe initialization
		private static Block[] _idToBlock;
		private static System.Collections.Generic.Dictionary<Block, byte> _blockToId;
		private static byte _nextId = 1;
		private static readonly object _blockMappingLock = new object();

		private int _sx, _sy, _sz;
		private int _strideXZ;
		private int _strideY;

		public Vector2I ChunkPosition { get; private set; }

		// RenderingServer optimization
		private Rid _meshRid = new Rid();
		private Rid _instanceRid = new Rid();
		
		// Mesh data pooling
		private static readonly System.Collections.Generic.Stack<MeshData> _meshDataPool = new();
		private const int MaxPooledMeshData = 8;
		
		private MeshData _pendingMesh;
		private int _pendingMeshVersion;
		private int _buildVersion;
		
		private static readonly SemaphoreSlim _buildSem = new(System.Environment.ProcessorCount, System.Environment.ProcessorCount);
		
		// CRITICAL FIX: Track if chunk is initialized
		private bool _isInitialized = false;

		public void UpdateChunkPosition(Vector2I newPosition)
		{
			ChunkPosition = newPosition;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct BlockUv
		{
			public Vector2 Side;
			public Vector2 Top;
			public Vector2 Bottom;
		}

		private sealed class MeshData
		{
			// OPTIMIZATION: Pre-allocated arrays that grow as needed
			public Vector3[] Vertices;
			public Vector3[] Normals;
			public Vector2[] UVs;
			public int[] Indices;
			public int VertexCount;
			public int IndexCount;

			public MeshData(int vertCapacity = 8192, int indexCapacity = 12288)
			{
				Vertices = new Vector3[vertCapacity];
				Normals = new Vector3[vertCapacity];
				UVs = new Vector2[vertCapacity];
				Indices = new int[indexCapacity];
			}

			public void Clear()
			{
				VertexCount = 0;
				IndexCount = 0;
			}

			public void EnsureVertexCapacity(int additional)
			{
				int required = VertexCount + additional;
				if (required > Vertices.Length)
				{
					int newSize = NextPowerOfTwo(required);
					Array.Resize(ref Vertices, newSize);
					Array.Resize(ref Normals, newSize);
					Array.Resize(ref UVs, newSize);
				}
			}

			public void EnsureIndexCapacity(int additional)
			{
				int required = IndexCount + additional;
				if (required > Indices.Length)
				{
					Array.Resize(ref Indices, NextPowerOfTwo(required));
				}
			}

			private static int NextPowerOfTwo(int value)
			{
				value--;
				value |= value >> 1;
				value |= value >> 2;
				value |= value >> 4;
				value |= value >> 8;
				value |= value >> 16;
				return value + 1;
			}
		}

		public override void _Ready()
		{
			// CRITICAL FIX: Validate dimensions
			if (Dimensions.X <= 0 || Dimensions.Y <= 0 || Dimensions.Z <= 0)
			{
				GD.PushError($"Invalid chunk dimensions: {Dimensions}");
				Dimensions = new Vector3I(16, 65, 16);
			}

			_sx = Dimensions.X;
			_sy = Dimensions.Y;
			_sz = Dimensions.Z;
			_strideXZ = _sx * _sz;
			_strideY = _strideXZ;

			// CRITICAL FIX: Validate block mapping exists
			if (_idToBlock == null || _blockToId == null)
			{
				GD.PushError("CRITICAL: Block mapping not initialized! Call Chunk.InitializeBlockMapping() first!");
				return;
			}

			_blockIds = new byte[_sx * _sy * _sz];

			ChunkPosition = new Vector2I(
				Mathf.FloorToInt(GlobalPosition.X / _sx),
				Mathf.FloorToInt(GlobalPosition.Z / _sz)
			);

			// RenderingServer setup
			if (MeshInstance != null)
			{
				_meshRid = RenderingServer.MeshCreate();
				_instanceRid = MeshInstance.GetInstance();
				
				// CRITICAL FIX: Validate RID before use
				if (_instanceRid.IsValid)
				{
					RenderingServer.InstanceSetBase(_instanceRid, _meshRid);
				}

				MeshInstance.CastShadow = ShadowCasting switch
				{
					0 => GeometryInstance3D.ShadowCastingSetting.Off,
					2 => GeometryInstance3D.ShadowCastingSetting.DoubleSided,
					_ => GeometryInstance3D.ShadowCastingSetting.On
				};
			}

			_isInitialized = true;
			GenerateBlocks();
			Rebuild();
		}

		public override void _ExitTree()
		{
			if (_meshRid.IsValid)
			{
				RenderingServer.FreeRid(_meshRid);
				_meshRid = new Rid();
			}
		}

		// CRITICAL FIX: Thread-safe block mapping initialization
		public static void InitializeBlockMapping(Block air, Block stone, Block dirt, Block grass)
		{
			lock (_blockMappingLock)
			{
				if (_idToBlock != null) return;

				_idToBlock = new Block[256];
				_blockToId = new System.Collections.Generic.Dictionary<Block, byte>();

				// CRITICAL FIX: Validate air block
				if (air == null)
				{
					GD.PushError("CRITICAL: Air block is null!");
					return;
				}

				_idToBlock[0] = air;
				_blockToId[air] = 0;

				RegisterBlock(stone);
				RegisterBlock(dirt);
				RegisterBlock(grass);

				GD.Print($"✓ Block mapping initialized: Air={air.ResourceName}, blocks registered={_blockToId.Count}");
			}
		}

		private static void RegisterBlock(Block block)
		{
			if (block == null)
			{
				GD.PushWarning("Attempted to register null block");
				return;
			}
			
			if (_blockToId.ContainsKey(block)) return;
			
			if (_nextId >= 255)
			{
				GD.PushError("CRITICAL: Cannot register more than 255 block types!");
				return;
			}

			_blockToId[block] = _nextId;
			_idToBlock[_nextId] = block;
			_nextId++;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private int Idx(int x, int y, int z) => x + _sx * (z + _sz * y);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private Block GetBlockById(byte id)
		{
			// CRITICAL FIX: Null safety check
			if (_idToBlock == null || id >= _idToBlock.Length)
				return null;
			return _idToBlock[id];
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private byte GetIdForBlock(Block block)
		{
			// CRITICAL FIX: Null safety check
			if (block == null || _blockToId == null)
				return 0; // Return air ID
			
			return _blockToId.TryGetValue(block, out byte id) ? id : (byte)0;
		}

		public void GenerateBlocks()
		{
			// CRITICAL FIX: Validate initialization
			if (!_isInitialized || _blockIds == null)
			{
				GD.PushError("Cannot generate blocks - chunk not initialized");
				return;
			}

			var bm = BlockManager.Instance;
			if (bm == null)
			{
				GD.PushError("CRITICAL: BlockManager.Instance is null!");
				return;
			}

			byte airId = GetIdForBlock(bm.Air);
			byte stoneId = GetIdForBlock(bm.Stone);
			byte dirtId = GetIdForBlock(bm.Dirt);
			byte grassId = GetIdForBlock(bm.Grass);

			int worldBaseX = ChunkPosition.X * _sx;
			int worldBaseZ = ChunkPosition.Y * _sz;

			// CRITICAL FIX: Heap allocation instead of stack for safety
			int[] heightMap = new int[_sx * _sz];
			
			// CRITICAL FIX: Validate Noise exists
			if (Noise == null)
			{
				GD.PushWarning($"Chunk {ChunkPosition}: No noise generator, generating flat terrain");
				Array.Fill(heightMap, _sy / 2);
			}
			else
			{
				// Generate height map
				for (int z = 0; z < _sz; z++)
				{
					int wz = worldBaseZ + z;
					for (int x = 0; x < _sx; x++)
					{
						int wx = worldBaseX + x;
						heightMap[x + _sx * z] = (int)(_sy * ((Noise.GetNoise2D(wx, wz) + 1f) * 0.5f));
					}
				}
			}

			// Fill blocks
			for (int z = 0; z < _sz; z++)
			{
				for (int x = 0; x < _sx; x++)
				{
					int groundHeight = heightMap[x + _sx * z];
					int groundHalfHeight = groundHeight >> 1;

					int idx = Idx(x, 0, z);
					for (int y = 0; y < _sy; y++, idx += _strideY)
					{
						byte blockId;
						if (y < groundHalfHeight)
							blockId = stoneId;
						else if (y < groundHeight)
							blockId = dirtId;
						else if (y == groundHeight)
							blockId = grassId;
						else
							blockId = airId;

						_blockIds[idx] = blockId;
					}
				}
			}
		}

		public void Rebuild()
		{
			// CRITICAL FIX: Validate chunk is ready
			if (!_isInitialized || _blockIds == null)
			{
				GD.PushWarning($"Chunk {ChunkPosition}: Cannot rebuild - not initialized");
				return;
			}

			int version = Interlocked.Increment(ref _buildVersion);
			if (AsyncBuild)
				_ = BuildMeshAsync(version);
			else
				ApplyMesh(BuildMeshData(BuildContextFromBlocks()), version);
		}

		private System.Collections.Generic.Dictionary<byte, BlockUv> BuildContextFromBlocks()
		{
			var bm = BlockManager.Instance;
			if (bm == null)
			{
				GD.PushError("CRITICAL: BlockManager.Instance is null during mesh build!");
				return new System.Collections.Generic.Dictionary<byte, BlockUv>();
			}

			var map = new System.Collections.Generic.Dictionary<byte, BlockUv>(16);

			// Scan unique block IDs
			var uniqueIds = new System.Collections.Generic.HashSet<byte>();
			for (int i = 0; i < _blockIds.Length; i++)
			{
				uniqueIds.Add(_blockIds[i]);
			}

			foreach (byte id in uniqueIds)
			{
				var b = GetBlockById(id);
				if (b == null) continue;

				var side = bm.GetUV0(b.Texture);
				var top = bm.GetUV0(b.GetTopOrSide());
				var bottom = bm.GetUV0(b.GetBottomOrSide());

				map[id] = new BlockUv { Side = side, Top = top, Bottom = bottom };
			}

			return map;
		}

		private async Task BuildMeshAsync(int version)
		{
			await _buildSem.WaitAsync();
			try
			{
				var ctx = BuildContextFromBlocks();
				var data = await Task.Run(() => BuildMeshData(ctx));

				// CRITICAL FIX: Validate before deferred call
				if (!IsInstanceValid(this) || !IsInsideTree() || data == null)
				{
					ReturnMeshDataToPool(data);
					return;
				}

				_pendingMesh = data;
				_pendingMeshVersion = version;

				CallDeferred(MethodName.ApplyPendingMeshDeferred);
			}
			catch (Exception ex)
			{
				GD.PushError($"CRITICAL: Async mesh build failed for chunk {ChunkPosition}: {ex.Message}");
			}
			finally
			{
				_buildSem.Release();
			}
		}

		private void ApplyPendingMeshDeferred()
		{
			var data = _pendingMesh;
			var version = _pendingMeshVersion;
			_pendingMesh = null;
			ApplyMesh(data, version);
		}

		private MeshData GetPooledMeshData()
		{
			lock (_meshDataPool)
			{
				if (_meshDataPool.Count > 0)
					return _meshDataPool.Pop();
			}
			return new MeshData();
		}

		private static void ReturnMeshDataToPool(MeshData data)
		{
			if (data == null) return;
			
			lock (_meshDataPool)
			{
				if (_meshDataPool.Count < MaxPooledMeshData)
				{
					data.Clear();
					_meshDataPool.Push(data);
				}
			}
		}

		private MeshData BuildMeshData(System.Collections.Generic.Dictionary<byte, BlockUv> uv)
		{
			var bm = BlockManager.Instance;
			if (bm == null)
			{
				GD.PushError("CRITICAL: BlockManager is null in BuildMeshData!");
				return new MeshData();
			}

			byte airId = GetIdForBlock(bm.Air);

			var meshData = GetPooledMeshData();
			meshData.Clear();

			// Build mesh faces
			try
			{
				GreedyLayerY(true, meshData, airId, uv);
				GreedyLayerY(false, meshData, airId, uv);
				GreedyLayerX(true, meshData, airId, uv);
				GreedyLayerX(false, meshData, airId, uv);
				GreedyLayerZ(true, meshData, airId, uv);
				GreedyLayerZ(false, meshData, airId, uv);
			}
			catch (Exception ex)
			{
				GD.PushError($"CRITICAL: Greedy meshing failed for chunk {ChunkPosition}: {ex.Message}");
			}

			return meshData;
		}

		// CRITICAL FIX: Heap allocation instead of stack
		private void GreedyLayerY(bool positive, MeshData meshData, byte airId, System.Collections.Generic.Dictionary<byte, BlockUv> uvMap)
		{
			int layerLen = _sx * _sz;
			
			// CRITICAL FIX: Use heap allocation for large chunks
			byte[] layerBlock = new byte[layerLen];
			int[] maskStamp = new int[layerLen];
			int[] usedStamp = new int[layerLen];
			int stamp = 1;

			int dy = positive ? 1 : -1;

			for (int y = 0; y < _sy; y++, stamp++)
			{
				// Build mask
				for (int z = 0; z < _sz; z++)
				{
					for (int x = 0; x < _sx; x++)
					{
						int idx = Idx(x, y, z);
						byte blockId = _blockIds[idx];
						if (blockId == airId) continue;

						int ny = y + dy;
						bool neighborTransparent = ny < 0 || ny >= _sy || _blockIds[Idx(x, ny, z)] == airId;

						if (!neighborTransparent) continue;

						int li = x + _sx * z;
						maskStamp[li] = stamp;
						layerBlock[li] = blockId;
					}
				}

				// Greedy scan
				for (int z = 0; z < _sz; z++)
				{
					int row = _sx * z;
					for (int x = 0; x < _sx; x++)
					{
						int i = row + x;
						if (maskStamp[i] != stamp || usedStamp[i] == stamp) continue;

						byte blockId = layerBlock[i];

						// Find width
						int w = 1;
						while (x + w < _sx)
						{
							int ii = row + (x + w);
							if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != blockId) break;
							w++;
						}

						// Find height
						int h = 1;
						bool done = false;
						while (z + h < _sz && !done)
						{
							int row2 = _sx * (z + h);
							for (int xx = 0; xx < w; xx++)
							{
								int ii = row2 + (x + xx);
								if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != blockId)
								{
									done = true;
									break;
								}
							}
							if (!done) h++;
						}

						// Mark used
						for (int zz = 0; zz < h; zz++)
						{
							int r = _sx * (z + zz);
							for (int xx = 0; xx < w; xx++)
								usedStamp[r + (x + xx)] = stamp;
						}

						// CRITICAL FIX: Validate UV map contains block ID
						if (!uvMap.ContainsKey(blockId))
						{
							GD.PushWarning($"Chunk {ChunkPosition}: Missing UV for block ID {blockId}");
							continue;
						}

						var uv0 = positive ? uvMap[blockId].Top : uvMap[blockId].Bottom;
						AddYQuadGreedy(y, x, z, w, h, positive, uv0, meshData);
					}
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void AddYQuadGreedy(int y, int startX, int startZ, int width, int height, bool positive, Vector2 uv0, MeshData meshData)
		{
			float fy = positive ? (y + 1.0f) : y;
			float x0 = startX;
			float x1 = startX + width;
			float z0 = startZ;
			float z1 = startZ + height;

			Vector3 a, b, c, d, normal;

			if (positive)
			{
				a = new Vector3(x0, fy, z0);
				b = new Vector3(x1, fy, z0);
				c = new Vector3(x1, fy, z1);
				d = new Vector3(x0, fy, z1);
				normal = Vector3.Up;
			}
			else
			{
				a = new Vector3(x0, fy, z1);
				b = new Vector3(x1, fy, z1);
				c = new Vector3(x1, fy, z0);
				d = new Vector3(x0, fy, z0);
				normal = Vector3.Down;
			}

			AddQuadDirect(meshData, a, b, c, d, normal, uv0);
		}

		// CRITICAL FIX: Heap allocation
		private void GreedyLayerX(bool positive, MeshData meshData, byte airId, System.Collections.Generic.Dictionary<byte, BlockUv> uvMap)
		{
			int layerLen = _sy * _sz;
			byte[] layerBlock = new byte[layerLen];
			int[] maskStamp = new int[layerLen];
			int[] usedStamp = new int[layerLen];
			int stamp = 1;

			int dx = positive ? 1 : -1;

			for (int x = 0; x < _sx; x++, stamp++)
			{
				for (int y = 0; y < _sy; y++)
				{
					for (int z = 0; z < _sz; z++)
					{
						byte blockId = _blockIds[Idx(x, y, z)];
						if (blockId == airId) continue;

						int nx = x + dx;
						bool neighborTransparent = nx < 0 || nx >= _sx || _blockIds[Idx(nx, y, z)] == airId;

						if (!neighborTransparent) continue;

						int li = y + _sy * z;
						maskStamp[li] = stamp;
						layerBlock[li] = blockId;
					}
				}

				for (int y = 0; y < _sy; y++)
				{
					for (int z = 0; z < _sz; z++)
					{
						int i = y + _sy * z;
						if (maskStamp[i] != stamp || usedStamp[i] == stamp) continue;

						byte blockId = layerBlock[i];

						int w = 1;
						while (z + w < _sz)
						{
							int ii = y + _sy * (z + w);
							if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != blockId) break;
							w++;
						}

						int h = 1;
						bool done = false;
						while (y + h < _sy && !done)
						{
							for (int zz = 0; zz < w; zz++)
							{
								int ii = (y + h) + _sy * (z + zz);
								if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != blockId)
								{
									done = true;
									break;
								}
							}
							if (!done) h++;
						}

						for (int yy = 0; yy < h; yy++)
							for (int zz = 0; zz < w; zz++)
								usedStamp[(y + yy) + _sy * (z + zz)] = stamp;

						if (!uvMap.ContainsKey(blockId)) continue;
						var uv0 = uvMap[blockId].Side;
						AddXQuadGreedy(x, y, z, h, w, positive, uv0, meshData);
					}
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void AddXQuadGreedy(int x, int startY, int startZ, int height, int width, bool positive, Vector2 uv0, MeshData meshData)
		{
			float fx = positive ? (x + 1.0f) : x;
			float y0 = startY;
			float y1 = startY + height;
			float z0 = startZ;
			float z1 = startZ + width;

			Vector3 a, b, c, d, normal;

			if (positive)
			{
				a = new Vector3(fx, y1, z0);
				b = new Vector3(fx, y0, z0);
				c = new Vector3(fx, y0, z1);
				d = new Vector3(fx, y1, z1);
				normal = Vector3.Right;
			}
			else
			{
				a = new Vector3(fx, y1, z1);
				b = new Vector3(fx, y0, z1);
				c = new Vector3(fx, y0, z0);
				d = new Vector3(fx, y1, z0);
				normal = Vector3.Left;
			}

			AddQuadDirect(meshData, a, b, c, d, normal, uv0);
		}

		// CRITICAL FIX: Heap allocation
		private void GreedyLayerZ(bool positive, MeshData meshData, byte airId, System.Collections.Generic.Dictionary<byte, BlockUv> uvMap)
		{
			int layerLen = _sx * _sy;
			byte[] layerBlock = new byte[layerLen];
			int[] maskStamp = new int[layerLen];
			int[] usedStamp = new int[layerLen];
			int stamp = 1;

			int dz = positive ? 1 : -1;

			for (int z = 0; z < _sz; z++, stamp++)
			{
				for (int y = 0; y < _sy; y++)
				{
					for (int x = 0; x < _sx; x++)
					{
						byte blockId = _blockIds[Idx(x, y, z)];
						if (blockId == airId) continue;

						int nz = z + dz;
						bool neighborTransparent = nz < 0 || nz >= _sz || _blockIds[Idx(x, y, nz)] == airId;

						if (!neighborTransparent) continue;

						int li = x + _sx * y;
						maskStamp[li] = stamp;
						layerBlock[li] = blockId;
					}
				}

				for (int y = 0; y < _sy; y++)
				{
					int row = _sx * y;
					for (int x = 0; x < _sx; x++)
					{
						int i = row + x;
						if (maskStamp[i] != stamp || usedStamp[i] == stamp) continue;

						byte blockId = layerBlock[i];

						int w = 1;
						while (x + w < _sx)
						{
							int ii = row + (x + w);
							if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != blockId) break;
							w++;
						}

						int h = 1;
						bool done = false;
						while (y + h < _sy && !done)
						{
							int row2 = _sx * (y + h);
							for (int xx = 0; xx < w; xx++)
							{
								int ii = row2 + (x + xx);
								if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != blockId)
								{
									done = true;
									break;
								}
							}
							if (!done) h++;
						}

						for (int yy = 0; yy < h; yy++)
						{
							int r = _sx * (y + yy);
							for (int xx = 0; xx < w; xx++)
								usedStamp[r + (x + xx)] = stamp;
						}

						if (!uvMap.ContainsKey(blockId)) continue;
						var uv0 = uvMap[blockId].Side;
						AddZQuadGreedy(z, x, y, w, h, positive, uv0, meshData);
					}
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void AddZQuadGreedy(int z, int startX, int startY, int width, int height, bool positive, Vector2 uv0, MeshData meshData)
		{
			float fz = positive ? (z + 1.0f) : z;
			float x0 = startX;
			float x1 = startX + width;
			float y0 = startY;
			float y1 = startY + height;

			Vector3 a, b, c, d, normal;

			if (positive)
			{
				a = new Vector3(x1, y1, fz);
				b = new Vector3(x1, y0, fz);
				c = new Vector3(x0, y0, fz);
				d = new Vector3(x0, y1, fz);
				normal = Vector3.Forward;
			}
			else
			{
				a = new Vector3(x0, y1, fz);
				b = new Vector3(x0, y0, fz);
				c = new Vector3(x1, y0, fz);
				d = new Vector3(x1, y1, fz);
				normal = Vector3.Back;
			}

			AddQuadDirect(meshData, a, b, c, d, normal, uv0);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void AddQuadDirect(MeshData meshData, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 uv0)
		{
			meshData.EnsureVertexCapacity(4);
			meshData.EnsureIndexCapacity(6);

			int start = meshData.VertexCount;

			meshData.Vertices[start] = a;
			meshData.Vertices[start + 1] = b;
			meshData.Vertices[start + 2] = c;
			meshData.Vertices[start + 3] = d;

			meshData.Normals[start] = normal;
			meshData.Normals[start + 1] = normal;
			meshData.Normals[start + 2] = normal;
			meshData.Normals[start + 3] = normal;

			meshData.UVs[start] = uv0;
			meshData.UVs[start + 1] = uv0;
			meshData.UVs[start + 2] = uv0;
			meshData.UVs[start + 3] = uv0;

			int idxPos = meshData.IndexCount;
			meshData.Indices[idxPos] = start;
			meshData.Indices[idxPos + 1] = start + 1;
			meshData.Indices[idxPos + 2] = start + 2;
			meshData.Indices[idxPos + 3] = start;
			meshData.Indices[idxPos + 4] = start + 2;
			meshData.Indices[idxPos + 5] = start + 3;

			meshData.VertexCount += 4;
			meshData.IndexCount += 6;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool InBounds(Vector3I p) =>
			p.X >= 0 && p.X < _sx &&
			p.Y >= 0 && p.Y < _sy &&
			p.Z >= 0 && p.Z < _sz;

		public Block GetBlockLocal(Vector3I p)
		{
			if (!InBounds(p)) return null;
			return GetBlockById(_blockIds[Idx(p.X, p.Y, p.Z)]);
		}

		public bool SetBlockLocal(Vector3I p, Block block)
		{
			if (!InBounds(p)) return false;
			_blockIds[Idx(p.X, p.Y, p.Z)] = GetIdForBlock(block);
			return true;
		}

		private void ApplyMesh(MeshData data, int version)
		{
			// Version check
			if (version != _buildVersion) 
			{
				ReturnMeshDataToPool(data);
				return;
			}
			
			// Validation
			if (!IsInsideTree() || MeshInstance == null || data == null) 
			{
				ReturnMeshDataToPool(data);
				return;
			}

			// CRITICAL FIX: Validate mesh has data
			if (data.VertexCount == 0)
			{
				GD.Print($"Chunk {ChunkPosition}: No visible faces (all underground or air)");
				ReturnMeshDataToPool(data);
				return;
			}

			// RenderingServer mesh creation
			if (_meshRid.IsValid)
			{
				try
				{
					RenderingServer.MeshClear(_meshRid);

					var arrays = new Godot.Collections.Array();
					arrays.Resize((int)Mesh.ArrayType.Max);

					// Copy only used portion
					var verts = new Vector3[data.VertexCount];
					var norms = new Vector3[data.VertexCount];
					var uvs = new Vector2[data.VertexCount];
					var indices = new int[data.IndexCount];

					Array.Copy(data.Vertices, verts, data.VertexCount);
					Array.Copy(data.Normals, norms, data.VertexCount);
					Array.Copy(data.UVs, uvs, data.VertexCount);
					Array.Copy(data.Indices, indices, data.IndexCount);

					arrays[(int)Mesh.ArrayType.Vertex] = verts;
					arrays[(int)Mesh.ArrayType.Normal] = norms;
					arrays[(int)Mesh.ArrayType.TexUV] = uvs;
					arrays[(int)Mesh.ArrayType.Index] = indices;

					RenderingServer.MeshAddSurfaceFromArrays(_meshRid, (Godot.RenderingServer.PrimitiveType)Mesh.PrimitiveType.Triangles, arrays);
					
					var material = BlockManager.Instance?.ChunkMaterial;
					if (material != null)
					{
						RenderingServer.MeshSurfaceSetMaterial(_meshRid, 0, material.GetRid());
					}
				}
				catch (Exception ex)
				{
					GD.PushError($"CRITICAL: Failed to create mesh for chunk {ChunkPosition}: {ex.Message}");
				}
			}

			// Collision generation
			if (BuildCollision && CollisionShape != null && data.VertexCount > 0)
			{
				try
				{
					var shape = new ConcavePolygonShape3D();
					
					var faces = new Vector3[data.IndexCount];
					for (int i = 0; i < data.IndexCount; i++)
					{
						faces[i] = data.Vertices[data.Indices[i]];
					}
					shape.Data = faces;
					
					CollisionShape.Shape = shape;
					
					#if DEBUG
					GD.Print($"✓ Chunk {ChunkPosition}: Collision built ({data.IndexCount} indices)");
					#endif
				}
				catch (Exception ex)
				{
					GD.PushError($"CRITICAL: Failed to build collision for chunk {ChunkPosition}: {ex.Message}");
				}
			}
			else if (CollisionShape != null)
			{
				CollisionShape.Shape = null;
			}

			ReturnMeshDataToPool(data);
		}
	}
}
