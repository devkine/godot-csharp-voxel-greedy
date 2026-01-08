using Godot;

public partial class Player : CharacterBody3D
{
    [Export] public Node3D Head { get; set; }
    [Export] public Camera3D Camera { get; set; }
    [Export] public RayCast3D RayCast { get; set; }
    [Export] public MeshInstance3D BlockHighlight { get; set; }

    [ExportCategory("Feel")]
    [Export] public float MouseSensitivity = 0.08f;
    [Export] public float MoveSpeed = 5f;
    [Export] public float SprintMultiplier = 1.6f;
    [Export] public float Accel = 16f;
    [Export] public float AirControl = 0.25f;
    [Export] public float Gravity = -24f;
    [Export] public float JumpVelocity = 5.5f;
    [Export] public float MaxReach = 6f;

    [Export] public AudioStreamPlayer3D PlaceSound { get; set; }
    [Export] public AudioStreamPlayer3D BreakSound { get; set; }

    [ExportCategory("Blocks")]
    [Export] public Block PlaceBlock; // still available if you want to use it later

    private Vector2 _look; // X=pitch, Y=yaw
    private float _coyoteTime = 0.12f, _jumpBuffer = 0.12f;
    private float _coyoteTimer, _jumpBufferTimer;

    private Vector3I _targetBlock;      // te breken blok
    private Vector3I _placeBlockPos;    // te plaatsen blok
    private NeinCraft.Scene.Chunk.Chunk _targetChunk;

    // Reused AABB extents to avoid re-allocating vectors
    private static readonly Vector3 PlayerHalfExtents = new Vector3(0.4f, 0.9f, 0.4f);

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;

        if (RayCast != null)
        {
            RayCast.TargetPosition = Vector3.Forward * MaxReach;
            RayCast.Enabled = true; // let physics step update collisions
        }

        if (BlockHighlight != null)
        {
            BlockHighlight.Visible = false;
        }

        if (PlaceSound == null)
            PlaceSound = GetNodeOrNull<AudioStreamPlayer3D>("PlaceSound");

        if (BreakSound == null)
            BreakSound = GetNodeOrNull<AudioStreamPlayer3D>("BreakSound");
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Captured;
        }

        if (Input.MouseMode != Input.MouseModeEnum.Captured)
            return;

        if (e is InputEventMouseMotion mm)
        {
            _look.X = Mathf.Clamp(_look.X - mm.Relative.Y * MouseSensitivity, -89.9f, 89.9f);
            _look.Y -= mm.Relative.X * MouseSensitivity;

            if (Head != null)
                Head.RotationDegrees = new Vector3(_look.X, 0, 0);

            RotationDegrees = new Vector3(0, _look.Y, 0);
        }

        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (Input.IsActionJustPressed("place_block"))
            {
                TryPlaceBlock();
            }
            if (Input.IsActionJustPressed("break_block"))
            {
                TryBreakBlock();
            }
        }
    }

    // No heavy work here anymore – helps on low-spec machines
public override void _Process(double delta)
{
    if (RayCast == null)
        return;

    // Keep reach in sync (in case you change MaxReach later)
    RayCast.TargetPosition = Vector3.Forward * MaxReach;

    // Force update this frame so highlight follows the camera exactly
    RayCast.ForceRaycastUpdate();

    // Update target block + wireframe position
    UpdateBlockAim();
}

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // Jump feel: coyote + buffer
        if (IsOnFloor())
            _coyoteTimer = _coyoteTime;
        else
            _coyoteTimer = Mathf.Max(0, _coyoteTimer - dt);

        _jumpBufferTimer = Mathf.Max(0, _jumpBufferTimer - dt);
        if (Input.IsActionJustPressed("jump"))
            _jumpBufferTimer = _jumpBuffer;

        var vel = Velocity;

        if (!IsOnFloor())
        {
            vel.Y += Gravity * dt;
        }
        else if (_jumpBufferTimer > 0 && _coyoteTimer > 0)
        {
            vel.Y = JumpVelocity;
            _jumpBufferTimer = 0;
            _coyoteTimer = 0;
        }

        // Movement (sprint + accel/decel)
        Vector2 in2 = Input.GetVector("move_left", "move_right", "move_backward", "move_forward");

        Vector3 fwd = -GlobalTransform.Basis.Z;
        fwd.Y = 0;
        fwd = fwd.Normalized();

        Vector3 rgt = GlobalTransform.Basis.X;
        rgt.Y = 0;
        rgt = rgt.Normalized();

        float speed = MoveSpeed * (Input.IsActionPressed("sprint") ? SprintMultiplier : 1f);
        Vector3 target = (fwd * in2.Y + rgt * in2.X) * speed;

        float a = IsOnFloor() ? Accel : Accel * AirControl;

        vel.X = Mathf.MoveToward(vel.X, target.X, a * dt);
        vel.Z = Mathf.MoveToward(vel.Z, target.Z, a * dt);

        Velocity = vel;
        MoveAndSlide();
    }

    private void UpdateBlockAim()
    {
        _targetChunk = null;

        if (RayCast == null)
        {
            if (BlockHighlight != null)
                BlockHighlight.Visible = false;
            return;
        }

        if (!RayCast.IsColliding())
        {
            if (BlockHighlight != null)
                BlockHighlight.Visible = false;
            return;
        }

        // Vind de Chunk boven de collider in de scene tree
        Node node = RayCast.GetCollider() as Node;
        while (node != null && _targetChunk == null)
        {
            _targetChunk = node as NeinCraft.Scene.Chunk.Chunk;
            node = node.GetParent();
        }

        if (_targetChunk == null)
        {
            if (BlockHighlight != null)
                BlockHighlight.Visible = false;
            return;
        }

        Vector3 hit = RayCast.GetCollisionPoint();
        Vector3 nrm = RayCast.GetCollisionNormal();

        // - normal = binnenkant -> breken, + normal = buitenkant -> plaatsen
        Vector3 localBreak = _targetChunk.ToLocal(hit - nrm * 0.001f);
        Vector3 localPlace = _targetChunk.ToLocal(hit + nrm * 0.001f);

        _targetBlock = new Vector3I(
            Mathf.FloorToInt(localBreak.X),
            Mathf.FloorToInt(localBreak.Y),
            Mathf.FloorToInt(localBreak.Z)
        );

        _placeBlockPos = new Vector3I(
            Mathf.FloorToInt(localPlace.X),
            Mathf.FloorToInt(localPlace.Y),
            Mathf.FloorToInt(localPlace.Z)
        );

        if (BlockHighlight != null)
        {
            bool show = _targetChunk.InBounds(_placeBlockPos); // of _targetBlock als je dat wilt
            BlockHighlight.Visible = show;

            if (show)
            {
                Vector3 center = _targetChunk.ToGlobal(_placeBlockPos + new Vector3(0.5f, 0.5f, 0.5f));
                BlockHighlight.GlobalTransform = new Transform3D(Basis.Identity, center);
            }
        }
    }

    private void TryBreakBlock()
    {
        if (_targetChunk == null)
            return;

        // If block removal actually succeeded
        if (_targetChunk.SetBlockLocal(_targetBlock, BlockManager.Instance.Air))
        {
            _targetChunk.Rebuild();

            if (BreakSound != null)
            {
                BreakSound.GlobalPosition = _targetChunk.ToGlobal(_targetBlock + new Vector3(0.5f, 0.5f, 0.5f));
                BreakSound.PitchScale = 1.0f + (float)GD.RandRange(-0.1, 0.1);
                BreakSound.Play();
                // GD.Print("🔊 Break block sound played at " + BreakSound.GlobalPosition);
            }
            // else GD.Print("⚠️ BreakSound node is null or missing");
        }
    }

    private void TryPlaceBlock()
    {
        if (_targetChunk == null)
            return;

        var pos = _placeBlockPos;

        // Voor nu nog steeds Dirt, zoals in je originele code
        var block = BlockManager.Instance.Dirt;

        PlaceBlockSafe(_targetChunk, pos, block);
        PlaceSound?.Play();
    }

    private void PlaceBlockSafe(NeinCraft.Scene.Chunk.Chunk chunk, Vector3I pos, Block block)
    {
        if (!chunk.InBounds(pos))
            return;

        var worldPos = chunk.ToGlobal(pos);
        var blockAabb = new Aabb(worldPos, Vector3.One);

        var playerAabb = new Aabb(GlobalPosition - PlayerHalfExtents, PlayerHalfExtents * 2f);

        if (playerAabb.Intersects(blockAabb))
        {
            if (pos.Y < Mathf.FloorToInt(GlobalPosition.Y))
            {
                chunk.SetBlockLocal(pos, block);
                chunk.Rebuild();

                float topY = worldPos.Y + 1.0f;
                GlobalPosition = new Vector3(GlobalPosition.X, topY + 0.05f, GlobalPosition.Z);
                Velocity = Vector3.Zero;

                PlaceSound?.Play();
            }
            else
            {
                // GD.Print("⚠️ Block placement canceled (inside player).");
            }
        }
        else
        {
            chunk.SetBlockLocal(pos, block);
            chunk.Rebuild();

            if (PlaceSound != null)
            {
                PlaceSound.GlobalPosition = worldPos + new Vector3(0.5f, 0.5f, 0.5f);
                PlaceSound.PitchScale = 1.0f + (float)GD.RandRange(-0.1, 0.1);
                PlaceSound.Play();
                // GD.Print("✅ Place block sound played");
            }
        }
    }
}
