using Godot;
using System;
using System.Collections.Generic;
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

		// Flattened storage: much faster than Block[,,]
		private Block[] _blocks;

		private int _sx, _sy, _sz;
		private int _strideXZ; // sx*sz

		public Vector2I ChunkPosition { get; private set; }

		private readonly ArrayMesh _mesh = new();
		private readonly Godot.Collections.Array _meshArrays = new();
		private MeshData _pendingMesh;
		private int _pendingMeshVersion;

		private int _buildVersion;
		private static readonly SemaphoreSlim _buildSem = new(2); // limit concurrent background builds

		private struct BlockUv
		{
			public Vector2 Side;
			public Vector2 Top;
			public Vector2 Bottom;
		}

		private sealed class MeshData
		{
			public Vector3[] Vertices = Array.Empty<Vector3>();
			public Vector3[] Normals = Array.Empty<Vector3>();
			public Vector2[] UVs = Array.Empty<Vector2>();
			public int[] Indices = Array.Empty<int>();
		}

		public override void _Ready()
		{
			_sx = Dimensions.X;
			_sy = Dimensions.Y;
			_sz = Dimensions.Z;
			_strideXZ = _sx * _sz;

			_blocks = new Block[_sx * _sy * _sz];

			ChunkPosition = new Vector2I(
				Mathf.FloorToInt(GlobalPosition.X / _sx),
				Mathf.FloorToInt(GlobalPosition.Z / _sz)
			);

			if (MeshInstance != null)
			{
				MeshInstance.CastShadow = ShadowCasting switch
				{
					0 => GeometryInstance3D.ShadowCastingSetting.Off,
					2 => GeometryInstance3D.ShadowCastingSetting.DoubleSided,
					_ => GeometryInstance3D.ShadowCastingSetting.On
				};

				// reuse one ArrayMesh instance forever
				MeshInstance.Mesh = _mesh;
			}

			_meshArrays.Resize((int)Mesh.ArrayType.Max);

			GenerateBlocks();
			Rebuild();
		}

		private int Idx(int x, int y, int z) => x + _sx * (z + _sz * y);

		public void GenerateBlocks()
		{
			var bm = BlockManager.Instance;
			var air = bm.Air;
			var stone = bm.Stone;
			var dirt = bm.Dirt;
			var grass = bm.Grass;

			int worldBaseX = ChunkPosition.X * _sx;
			int worldBaseZ = ChunkPosition.Y * _sz;

			for (int x = 0; x < _sx; x++)
			{
				int wx = worldBaseX + x;
				for (int z = 0; z < _sz; z++)
				{
					int wz = worldBaseZ + z;
					int groundHeight = (int)(_sy * ((Noise.GetNoise2D(wx, wz) + 1f) * 0.5f));

					int idx = x + _sx * z; // y=0
					for (int y = 0; y < _sy; y++, idx += _strideXZ)
					{
						Block b;
						if (y < groundHeight / 2) b = stone;
						else if (y < groundHeight) b = dirt;
						else if (y == groundHeight) b = grass;
						else b = air;

						_blocks[idx] = b;
					}
				}
			}
		}

		public void Rebuild()
		{
			int version = Interlocked.Increment(ref _buildVersion);
			if (AsyncBuild)
				_ = BuildMeshAsync(version);
			else
				ApplyMesh(BuildMeshData(BuildContextFromBlocks()), version);
		}

		private Dictionary<Block, BlockUv> BuildContextFromBlocks()
		{
			// Build a tiny lookup (Block -> uv0 per face) on main thread (safe)
			var bm = BlockManager.Instance;
			var map = new Dictionary<Block, BlockUv>(16);

			for (int i = 0; i < _blocks.Length; i++)
			{
				var b = _blocks[i];
				if (b == null) continue;
				if (map.ContainsKey(b)) continue;

				var side = bm.GetUV0(b.Texture);
				var top = bm.GetUV0(b.GetTopOrSide());
				var bottom = bm.GetUV0(b.GetBottomOrSide());

				map[b] = new BlockUv { Side = side, Top = top, Bottom = bottom };
			}

			// Ensure air exists even if somehow not encountered
			if (bm.Air != null && !map.ContainsKey(bm.Air))
				map[bm.Air] = new BlockUv { Side = Vector2.Zero, Top = Vector2.Zero, Bottom = Vector2.Zero };

			return map;
		}

		private async Task BuildMeshAsync(int version)
		{
			await _buildSem.WaitAsync();
			try
			{
				var ctx = BuildContextFromBlocks();
				var data = await Task.Run(() => BuildMeshData(ctx));

				if (!IsInstanceValid(this) || !IsInsideTree())
					return;

				// store latest result; older ones will be ignored by version anyway
				_pendingMesh = data;
				_pendingMeshVersion = version;

				CallDeferred(nameof(ApplyPendingMeshDeferred));
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

			// optional: clear refs early
			_pendingMesh = null;

			ApplyMesh(data, version);
		}


		private void ApplyMeshDeferred(MeshData data, int version) => ApplyMesh(data, version);

		private MeshData BuildMeshData(Dictionary<Block, BlockUv> uv)
		{
			var bm = BlockManager.Instance;
			var air = bm.Air;

			// Capacity hint: greedy reduces face count a lot; keep it reasonable
			var vertices = new List<Vector3>(8192);
			var normals = new List<Vector3>(8192);
			var uvs = new List<Vector2>(8192);
			var indices = new List<int>(12288);

			GreedyLayerY(true, vertices, normals, uvs, indices, air, uv);
			GreedyLayerY(false, vertices, normals, uvs, indices, air, uv);

			GreedyLayerX(true, vertices, normals, uvs, indices, air, uv);
			GreedyLayerX(false, vertices, normals, uvs, indices, air, uv);

			GreedyLayerZ(true, vertices, normals, uvs, indices, air, uv);
			GreedyLayerZ(false, vertices, normals, uvs, indices, air, uv);

			return new MeshData
			{
				Vertices = vertices.ToArray(),
				Normals = normals.ToArray(),
				UVs = uvs.ToArray(),
				Indices = indices.ToArray()
			};
		}

		// ---------------- Y faces (greedy in X/Z) ----------------
		private void GreedyLayerY(
			bool positive,
			List<Vector3> verts,
			List<Vector3> norms,
			List<Vector2> uvs,
			List<int> indices,
			Block air,
			Dictionary<Block, BlockUv> uvMap)
		{
			int layerLen = _sx * _sz;
			var layerBlock = new Block[layerLen];
			var maskStamp = new int[layerLen];
			var usedStamp = new int[layerLen];
			int stamp = 1;

			int dy = positive ? 1 : -1;

			for (int y = 0; y < _sy; y++, stamp++)
			{
				// build mask
				for (int z = 0; z < _sz; z++)
				{
					for (int x = 0; x < _sx; x++)
					{
						var b = _blocks[Idx(x, y, z)];
						if (b == null || b == air) continue;

						int ny = y + dy;
						bool neighborTransparent =
							ny < 0 || ny >= _sy ||
							_blocks[Idx(x, ny, z)] == air;

						if (!neighborTransparent) continue;

						int li = x + _sx * z;
						maskStamp[li] = stamp;
						layerBlock[li] = b;
					}
				}

				// greedy scan
				for (int z = 0; z < _sz; z++)
				{
					int row = _sx * z;
					for (int x = 0; x < _sx; x++)
					{
						int i = row + x;
						if (maskStamp[i] != stamp || usedStamp[i] == stamp) continue;

						Block b = layerBlock[i];

						int w = 1;
						while (x + w < _sx)
						{
							int ii = row + (x + w);
							if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != b) break;
							w++;
						}

						int h = 1;
						bool done = false;
						while (z + h < _sz && !done)
						{
							int row2 = _sx * (z + h);
							for (int xx = 0; xx < w; xx++)
							{
								int ii = row2 + (x + xx);
								if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != b)
								{
									done = true;
									break;
								}
							}
							if (!done) h++;
						}

						for (int zz = 0; zz < h; zz++)
						{
							int r = _sx * (z + zz);
							for (int xx = 0; xx < w; xx++)
								usedStamp[r + (x + xx)] = stamp;
						}

						var uv0 = positive ? uvMap[b].Top : uvMap[b].Bottom;
						AddYQuadGreedy(y, x, z, w, h, positive, uv0, verts, norms, uvs, indices);
					}
				}
			}
		}

		private static void AddYQuadGreedy(
			int y, int startX, int startZ, int width, int height,
			bool positive, Vector2 uv0,
			List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> indices)
		{
			float fy = positive ? (y + 1.0f) : y;

			float x0 = startX;
			float x1 = startX + width;
			float z0 = startZ;
			float z1 = startZ + height;

			Vector3 a, b, c, d;
			Vector3 normal;

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

			AddQuadBase(verts, norms, uvs, indices, a, b, c, d, normal, uv0);
		}

		// ---------------- X faces (greedy in Y/Z) ----------------
		private void GreedyLayerX(
			bool positive,
			List<Vector3> verts,
			List<Vector3> norms,
			List<Vector2> uvs,
			List<int> indices,
			Block air,
			Dictionary<Block, BlockUv> uvMap)
		{
			int layerLen = _sy * _sz;
			var layerBlock = new Block[layerLen];
			var maskStamp = new int[layerLen];
			var usedStamp = new int[layerLen];
			int stamp = 1;

			int dx = positive ? 1 : -1;

			for (int x = 0; x < _sx; x++, stamp++)
			{
				for (int y = 0; y < _sy; y++)
				{
					for (int z = 0; z < _sz; z++)
					{
						var b = _blocks[Idx(x, y, z)];
						if (b == null || b == air) continue;

						int nx = x + dx;
						bool neighborTransparent =
							nx < 0 || nx >= _sx ||
							_blocks[Idx(nx, y, z)] == air;

						if (!neighborTransparent) continue;

						int li = y + _sy * z;
						maskStamp[li] = stamp;
						layerBlock[li] = b;
					}
				}

				for (int y = 0; y < _sy; y++)
				{
					for (int z = 0; z < _sz; z++)
					{
						int i = y + _sy * z;
						if (maskStamp[i] != stamp || usedStamp[i] == stamp) continue;

						Block b = layerBlock[i];

						int w = 1;
						while (z + w < _sz)
						{
							int ii = y + _sy * (z + w);
							if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != b) break;
							w++;
						}

						int h = 1;
						bool done = false;
						while (y + h < _sy && !done)
						{
							for (int zz = 0; zz < w; zz++)
							{
								int ii = (y + h) + _sy * (z + zz);
								if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != b)
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

						var uv0 = uvMap[b].Side;
						AddXQuadGreedy(x, y, z, h, w, positive, uv0, verts, norms, uvs, indices);
					}
				}
			}
		}

		private static void AddXQuadGreedy(
			int x, int startY, int startZ, int height, int width,
			bool positive, Vector2 uv0,
			List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> indices)
		{
			float fx = positive ? (x + 1.0f) : x;

			float y0 = startY;
			float y1 = startY + height;
			float z0 = startZ;
			float z1 = startZ + width;

			Vector3 a, b, c, d;
			Vector3 normal;

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

			AddQuadBase(verts, norms, uvs, indices, a, b, c, d, normal, uv0);
		}

		// ---------------- Z faces (greedy in X/Y) ----------------
		private void GreedyLayerZ(
			bool positive,
			List<Vector3> verts,
			List<Vector3> norms,
			List<Vector2> uvs,
			List<int> indices,
			Block air,
			Dictionary<Block, BlockUv> uvMap)
		{
			int layerLen = _sx * _sy;
			var layerBlock = new Block[layerLen];
			var maskStamp = new int[layerLen];
			var usedStamp = new int[layerLen];
			int stamp = 1;

			int dz = positive ? 1 : -1;

			for (int z = 0; z < _sz; z++, stamp++)
			{
				for (int y = 0; y < _sy; y++)
				{
					for (int x = 0; x < _sx; x++)
					{
						var b = _blocks[Idx(x, y, z)];
						if (b == null || b == air) continue;

						int nz = z + dz;
						bool neighborTransparent =
							nz < 0 || nz >= _sz ||
							_blocks[Idx(x, y, nz)] == air;

						if (!neighborTransparent) continue;

						int li = x + _sx * y;
						maskStamp[li] = stamp;
						layerBlock[li] = b;
					}
				}

				for (int y = 0; y < _sy; y++)
				{
					int row = _sx * y;
					for (int x = 0; x < _sx; x++)
					{
						int i = row + x;
						if (maskStamp[i] != stamp || usedStamp[i] == stamp) continue;

						Block b = layerBlock[i];

						int w = 1;
						while (x + w < _sx)
						{
							int ii = row + (x + w);
							if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != b) break;
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
								if (maskStamp[ii] != stamp || usedStamp[ii] == stamp || layerBlock[ii] != b)
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

						var uv0 = uvMap[b].Side;
						AddZQuadGreedy(z, x, y, w, h, positive, uv0, verts, norms, uvs, indices);
					}
				}
			}
		}

		private static void AddZQuadGreedy(
			int z, int startX, int startY, int width, int height,
			bool positive, Vector2 uv0,
			List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> indices)
		{
			float fz = positive ? (z + 1.0f) : z;

			float x0 = startX;
			float x1 = startX + width;
			float y0 = startY;
			float y1 = startY + height;

			Vector3 a, b, c, d;
			Vector3 normal;

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

			AddQuadBase(verts, norms, uvs, indices, a, b, c, d, normal, uv0);
		}

		private static void AddQuadBase(
			List<Vector3> verts,
			List<Vector3> norms,
			List<Vector2> uvs,
			List<int> indices,
			Vector3 a, Vector3 b, Vector3 c, Vector3 d,
			Vector3 normal,
			Vector2 uv0)
		{
			int start = verts.Count;

			verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);

			norms.Add(normal); norms.Add(normal); norms.Add(normal); norms.Add(normal);

			// shader uses uv0 as atlas cell origin
			uvs.Add(uv0); uvs.Add(uv0); uvs.Add(uv0); uvs.Add(uv0);

			indices.Add(start + 0);
			indices.Add(start + 1);
			indices.Add(start + 2);
			indices.Add(start + 0);
			indices.Add(start + 2);
			indices.Add(start + 3);
		}

		// ---- Public helpers ----
		public bool InBounds(Vector3I p) =>
			p.X >= 0 && p.X < _sx &&
			p.Y >= 0 && p.Y < _sy &&
			p.Z >= 0 && p.Z < _sz;

		public Block GetBlockLocal(Vector3I p)
		{
			if (!InBounds(p)) return null;
			return _blocks[Idx(p.X, p.Y, p.Z)];
		}

		public bool SetBlockLocal(Vector3I p, Block block)
		{
			if (!InBounds(p)) return false;
			_blocks[Idx(p.X, p.Y, p.Z)] = block;
			return true;
		}

		private void ApplyMesh(MeshData data, int version)
		{
			// drop stale async results
			if (version != _buildVersion) return;
			if (!IsInsideTree() || MeshInstance == null || data == null) return;

			_mesh.ClearSurfaces();

			_meshArrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
			_meshArrays[(int)Mesh.ArrayType.Normal] = data.Normals;
			_meshArrays[(int)Mesh.ArrayType.TexUV] = data.UVs;
			_meshArrays[(int)Mesh.ArrayType.Index] = data.Indices;

			_mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _meshArrays);
			_mesh.SurfaceSetMaterial(0, BlockManager.Instance.ChunkMaterial);

			if (BuildCollision && CollisionShape != null)
				CollisionShape.Shape = _mesh.CreateTrimeshShape();
			else if (CollisionShape != null)
				CollisionShape.Shape = null;
		}
	}
}
