using Godot;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[Tool]
public partial class BlockManager : Node
{
	[Export] public Block Air { get; set; }
	[Export] public Block Stone { get; set; }
	[Export] public Block Dirt { get; set; }
	[Export] public Block Grass { get; set; }

	[Export] public int GridWidth = 4;
	[Export] public Vector2I BlockTextureSize = new(16, 16);
	[Export] public bool Unshaded = false;

	public static BlockManager Instance { get; private set; }

	[Export] public Material ChunkMaterial { get; private set; }

	// OPTIMIZATION: Use arrays instead of Dictionary for O(1) lookups
	private readonly Vector2[] _uv0Cache = new Vector2[256];
	private readonly Dictionary<Texture2D, int> _textureLookup = new();
	
	private int _gridHeight;
	private float _invGridWidth;
	private float _invGridHeight;

	public Vector2 TextureAtlasSize { get; private set; }

	public override void _Ready()
	{
		Instance = this;

		// OPTIMIZATION: Pre-allocate with exact capacity
		var textures = new List<Texture2D>(16);
		var unique = new HashSet<Texture2D>();

		AddBlockTextures(Air, unique, textures);
		AddBlockTextures(Stone, unique, textures);
		AddBlockTextures(Dirt, unique, textures);
		AddBlockTextures(Grass, unique, textures);

		// OPTIMIZATION: Build lookup table
		for (int i = 0; i < textures.Count; i++)
		{
			_textureLookup[textures[i]] = i;
		}

		_gridHeight = Mathf.CeilToInt(textures.Count / (float)GridWidth);
		_invGridWidth = 1f / GridWidth;
		_invGridHeight = 1f / _gridHeight;

		// OPTIMIZATION: Use Image.Create instead of CreateEmpty for better performance
		var atlasImage = Image.Create(
			GridWidth * BlockTextureSize.X,
			_gridHeight * BlockTextureSize.Y,
			false,
			Image.Format.Rgba8
		);

		// OPTIMIZATION: Batch texture blitting
		for (int i = 0; i < textures.Count; i++)
		{
			int x = i % GridWidth;
			int y = i / GridWidth;

			var src = textures[i].GetImage();
			if (src.GetFormat() != Image.Format.Rgba8)
				src.Convert(Image.Format.Rgba8);

			atlasImage.BlitRect(
				src,
				new Rect2I(Vector2I.Zero, BlockTextureSize),
				new Vector2I(x * BlockTextureSize.X, y * BlockTextureSize.Y)
			);

			// OPTIMIZATION: Pre-calculate UV0 for all textures
			_uv0Cache[i] = new Vector2(x * _invGridWidth, y * _invGridHeight);
		}

		var atlasTexture = ImageTexture.CreateFromImage(atlasImage);

		// OPTIMIZATION: Try to use shader material, fallback to standard if not available
		var shader = ResourceLoader.Load<Shader>("res://Shaders/VoxelAtlas.gdshader");
		if (shader == null)
		{
			ChunkMaterial = CreateFallbackMaterial(atlasTexture);
		}
		else
		{
			ChunkMaterial = CreateShaderMaterial(shader, atlasTexture);
		}

		TextureAtlasSize = new Vector2(GridWidth, _gridHeight);
	}

	private Material CreateShaderMaterial(Shader shader, Texture2D atlasTexture)
	{
		var mat = new ShaderMaterial { Shader = shader };
		mat.SetShaderParameter("atlas_tex", atlasTexture);
		mat.SetShaderParameter("grid_width", GridWidth);
		mat.SetShaderParameter("grid_height", _gridHeight);
		
		// OPTIMIZATION: Pass pre-calculated inverse grid size
		mat.SetShaderParameter("inv_grid_size", new Vector2(_invGridWidth, _invGridHeight));
		
		return mat;
	}

	private Material CreateFallbackMaterial(Texture2D atlasTexture)
	{
		return new StandardMaterial3D
		{
			AlbedoTexture = atlasTexture,
			TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
			Roughness = 1.0f,
			Metallic = 0.0f,
			ShadingMode = Unshaded
				? BaseMaterial3D.ShadingModeEnum.Unshaded
				: StandardMaterial3D.ShadingModeEnum.PerPixel,
			// OPTIMIZATION: Disable features we don't use
			DisableReceiveShadows = true,
			CullMode = BaseMaterial3D.CullModeEnum.Back
		};
	}

	private static void AddBlockTextures(Block b, HashSet<Texture2D> unique, List<Texture2D> outList)
	{
		if (b == null) return;

		// OPTIMIZATION: Inline texture collection
		if (b.Texture != null && unique.Add(b.Texture))
			outList.Add(b.Texture);
		
		if (b.TopTexture != null && unique.Add(b.TopTexture))
			outList.Add(b.TopTexture);
		
		if (b.BottomTexture != null && unique.Add(b.BottomTexture))
			outList.Add(b.BottomTexture);
	}

	// OPTIMIZATION: Inlined for hot path, array lookup instead of dictionary
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Vector2 GetUV0(Texture2D texture)
	{
		if (texture == null) return Vector2.Zero;
		
		if (_textureLookup.TryGetValue(texture, out int index))
			return _uv0Cache[index];
		
		return Vector2.Zero;
	}
}
