using Godot;
using System.Collections.Generic;

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

    private readonly Dictionary<Texture2D, Vector2I> _atlasCell = new();
    private readonly Dictionary<Texture2D, Vector2> _uv0 = new();

    private int _gridHeight;
    private float _invGridWidth;
    private float _invGridHeight;

    public Vector2 TextureAtlasSize { get; private set; }

    public override void _Ready()
    {
        Instance = this;

        // Gather unique textures (no LINQ, no allocations per texture property)
        var textures = new List<Texture2D>(32);
        var unique = new HashSet<Texture2D>();

        AddBlockTextures(Air, unique, textures);
        AddBlockTextures(Stone, unique, textures);
        AddBlockTextures(Dirt, unique, textures);
        AddBlockTextures(Grass, unique, textures);

        for (int i = 0; i < textures.Count; i++)
            _atlasCell[textures[i]] = new Vector2I(i % GridWidth, i / GridWidth);

        _gridHeight = Mathf.CeilToInt(textures.Count / (float)GridWidth);
        _invGridWidth = 1f / GridWidth;
        _invGridHeight = 1f / _gridHeight;

        var atlasImage = Image.CreateEmpty(
            GridWidth * BlockTextureSize.X,
            _gridHeight * BlockTextureSize.Y,
            false,
            Image.Format.Rgba8
        );

        for (int y = 0; y < _gridHeight; y++)
        {
            for (int x = 0; x < GridWidth; x++)
            {
                int idx = x + y * GridWidth;
                if (idx >= textures.Count) break;

                var src = textures[idx].GetImage();
                if (src.GetFormat() != Image.Format.Rgba8)
                    src.Convert(Image.Format.Rgba8);

                atlasImage.BlitRect(
                    src,
                    new Rect2I(Vector2I.Zero, BlockTextureSize),
                    new Vector2I(x * BlockTextureSize.X, y * BlockTextureSize.Y)
                );
            }
        }

        var atlasTexture = ImageTexture.CreateFromImage(atlasImage);

        var shader = ResourceLoader.Load<Shader>("res://Shaders/VoxelAtlas.gdshader");
        if (shader == null)
        {
            var fallback = new StandardMaterial3D
            {
                AlbedoTexture = atlasTexture,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                Roughness = 1.0f,
                Metallic = 0.0f,
                ShadingMode = Unshaded
                    ? BaseMaterial3D.ShadingModeEnum.Unshaded
                    : StandardMaterial3D.ShadingModeEnum.PerPixel
            };
            ChunkMaterial = fallback;
        }
        else
        {
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("atlas_tex", atlasTexture);
            mat.SetShaderParameter("grid_width", GridWidth);
            mat.SetShaderParameter("grid_height", _gridHeight);
            ChunkMaterial = mat;
        }

        TextureAtlasSize = new Vector2(GridWidth, _gridHeight);

        // Cache UV0 (cell origin) once. No per-quad division later.
        _uv0.Clear();
        foreach (var tex in textures)
        {
            var cell = _atlasCell[tex];
            _uv0[tex] = new Vector2(cell.X * _invGridWidth, cell.Y * _invGridHeight);
        }
    }

    private static void AddBlockTextures(Block b, HashSet<Texture2D> unique, List<Texture2D> outList)
    {
        if (b == null) return;

        var tmp = new List<Texture2D>(3);
        b.CollectTextures(tmp);

        for (int i = 0; i < tmp.Count; i++)
        {
            var t = tmp[i];
            if (t == null) continue;
            if (unique.Add(t))
                outList.Add(t);
        }
    }

    public Vector2 GetUV0(Texture2D texture)
    {
        if (texture == null) return Vector2.Zero;
        return _uv0.TryGetValue(texture, out var v) ? v : Vector2.Zero;
    }
}
