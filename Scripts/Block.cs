using Godot;

[Tool]
[GlobalClass]
public partial class Block : Resource
{
    [Export] public Texture2D Texture { get; set; }
    [Export] public Texture2D TopTexture { get; set; }
    [Export] public Texture2D BottomTexture { get; set; }

    // No allocations, used by BlockManager at startup
    public void CollectTextures(System.Collections.Generic.List<Texture2D> dst)
    {
        if (Texture != null) dst.Add(Texture);
        if (TopTexture != null) dst.Add(TopTexture);
        if (BottomTexture != null) dst.Add(BottomTexture);
    }

    public Texture2D GetTopOrSide() => TopTexture ?? Texture;
    public Texture2D GetBottomOrSide() => BottomTexture ?? Texture;
}
