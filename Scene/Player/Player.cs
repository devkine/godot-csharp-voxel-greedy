using Godot;
using System.Runtime.CompilerServices;

public partial class Player : CharacterBody3D
{
    [Export] public Node3D Head { get; set; }
    [Export] public Camera3D Camera { get; set; }
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

    [ExportCategory("Performance")]
    [Export(PropertyHint.Range, "0,10,1")] 
    public int RaycastSkipFrames = 2; // Update every 3rd frame by default
    
    [Export] 
    public bool EnableBlockHighlight = true; // Can disable for extra perf
    
    [Export]
    public bool UseManualRaycast = true; // Use optimized manual raycast instead of RayCast3D node

    private Vector2 _look; // X=pitch, Y=yaw
    private const float CoyoteTime = 0.12f;
    private const float JumpBuffer = 0.12f;
    private float _coyoteTimer, _jumpBufferTimer;

    // Block targeting (cached between frames)
    private Vector3I _targetBlock;      
    private Vector3I _placeBlockPos;    
    private NeinCraft.Scene.Chunk.Chunk _targetChunk;

    // Frame counter for raycast updates
    private int _raycastFrameCounter = 0;

    // Reused AABB extents to avoid re-allocating vectors
    private static readonly Vector3 PlayerHalfExtents = new Vector3(0.4f, 0.9f, 0.4f);
    
    // Cached physics raycast params (reused every raycast)
    private PhysicsRayQueryParameters3D _rayQueryParams;
    private PhysicsDirectSpaceState3D _spaceState;
    
    // Cache frequently accessed transforms
    private Transform3D _headTransform;
    private Basis _globalBasis;
    
    // Input caching to avoid multiple Input calls
    private bool _wantsJump;
    private bool _wantsSprint;
    private Vector2 _moveInput;
    
    // Pre-allocated for place/break to avoid allocations
    private Aabb _blockAabb;
    private Aabb _playerAabb;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;

        if (BlockHighlight != null)
        {
            BlockHighlight.Visible = false;
        }

        if (PlaceSound == null)
            PlaceSound = GetNodeOrNull<AudioStreamPlayer3D>("PlaceSound");

        if (BreakSound == null)
            BreakSound = GetNodeOrNull<AudioStreamPlayer3D>("BreakSound");
        
        // Setup physics raycast params once
        if (UseManualRaycast)
        {
            _rayQueryParams = PhysicsRayQueryParameters3D.Create(Vector3.Zero, Vector3.Zero);
            _rayQueryParams.CollideWithAreas = false;
            _rayQueryParams.CollideWithBodies = true;
            _spaceState = GetWorld3D().DirectSpaceState;
        }
        
        // Pre-allocate AABBs
        _blockAabb = new Aabb(Vector3.Zero, Vector3.One);
        _playerAabb = new Aabb(Vector3.Zero, PlayerHalfExtents * 2f);
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

        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                TryBreakBlock();
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                TryPlaceBlock();
            }
        }
    }
    
    // OPTIMIZATION: Separate input handling into its own method
    // Called only when mouse moves to avoid constant transform updates
    public override void _Input(InputEvent e)
    {
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
    }

    // CRITICAL: Minimize work in _Process - this is called every frame
    public override void _Process(double delta)
    {
        // Skip raycast update most frames
        _raycastFrameCounter++;
        if (_raycastFrameCounter <= RaycastSkipFrames)
            return;
        _raycastFrameCounter = 0;

        // Only update if highlighting is enabled
        if (EnableBlockHighlight && Camera != null)
            UpdateBlockAim();
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // Cache input once per physics frame
        _wantsJump = Input.IsActionPressed("jump");
        _wantsSprint = Input.IsActionPressed("sprint");
        _moveInput = Input.GetVector("move_left", "move_right", "move_backward", "move_forward");

        // Jump system
        UpdateJumpState(dt);

        var vel = Velocity;

        // Gravity
        if (!IsOnFloor())
            vel.Y += Gravity * dt;
        else if (_jumpBufferTimer > 0 && _coyoteTimer > 0)
        {
            vel.Y = JumpVelocity;
            _jumpBufferTimer = 0;
            _coyoteTimer = 0;
        }

        // Movement
        UpdateMovement(ref vel, dt);

        Velocity = vel;
        MoveAndSlide();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateJumpState(float dt)
    {
        if (IsOnFloor())
            _coyoteTimer = CoyoteTime;
        else
            _coyoteTimer = Mathf.Max(0, _coyoteTimer - dt);

        _jumpBufferTimer = Mathf.Max(0, _jumpBufferTimer - dt);
        if (_wantsJump && _jumpBufferTimer == 0)
            _jumpBufferTimer = JumpBuffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateMovement(ref Vector3 vel, float dt)
    {
        // Early exit if no movement input
        if (_moveInput.LengthSquared() < 0.001f)
        {
            float a = IsOnFloor() ? Accel : Accel * AirControl;
            vel.X = Mathf.MoveToward(vel.X, 0, a * dt);
            vel.Z = Mathf.MoveToward(vel.Z, 0, a * dt);
            return;
        }

        // Cache basis to avoid multiple property accesses
        _globalBasis = GlobalTransform.Basis;
        
        // Calculate movement direction
        Vector3 fwd = -_globalBasis.Z;
        fwd.Y = 0;
        fwd = fwd.Normalized();

        Vector3 rgt = _globalBasis.X;
        rgt.Y = 0;
        rgt = rgt.Normalized();

        float speed = MoveSpeed * (_wantsSprint ? SprintMultiplier : 1f);
        Vector3 target = (fwd * _moveInput.Y + rgt * _moveInput.X) * speed;

        float accel = IsOnFloor() ? Accel : Accel * AirControl;

        vel.X = Mathf.MoveToward(vel.X, target.X, accel * dt);
        vel.Z = Mathf.MoveToward(vel.Z, target.Z, accel * dt);
    }

    // ULTRA-OPTIMIZED: Manual DDA raycast instead of physics raycast
    private void UpdateBlockAim()
    {
        _targetChunk = null;

        if (!UseManualRaycast)
        {
            UpdateBlockAimPhysics();
            return;
        }

        // DDA Voxel Traversal - much faster than physics raycast
        // Based on "A Fast Voxel Traversal Algorithm" by John Amanatides
        
        Vector3 origin = Camera.GlobalPosition;
        Vector3 direction = -Camera.GlobalTransform.Basis.Z;
        
        // Start position in world space
        float t = 0;
        float maxDistance = MaxReach;
        
        // DDA step
        Vector3 pos = origin;
        Vector3 step = direction.Sign();
        
        // Calculate delta T per axis
        Vector3 deltaDist = new Vector3(
            Mathf.Abs(1.0f / direction.X),
            Mathf.Abs(1.0f / direction.Y),
            Mathf.Abs(1.0f / direction.Z)
        );
        
        // Initial t max per axis
        Vector3I voxel = new Vector3I(
            Mathf.FloorToInt(pos.X),
            Mathf.FloorToInt(pos.Y),
            Mathf.FloorToInt(pos.Z)
        );
        
        Vector3 tMax = new Vector3(
            direction.X != 0 ? ((step.X > 0 ? voxel.X + 1 : voxel.X) - pos.X) / direction.X : float.MaxValue,
            direction.Y != 0 ? ((step.Y > 0 ? voxel.Y + 1 : voxel.Y) - pos.Y) / direction.Y : float.MaxValue,
            direction.Z != 0 ? ((step.Z > 0 ? voxel.Z + 1 : voxel.Z) - pos.Z) / direction.Z : float.MaxValue
        );
        
        Vector3 lastNormal = Vector3.Up;
        Vector3I lastVoxel = voxel;
        
        // Traverse voxels
        for (int i = 0; i < 100; i++) // Max 100 steps
        {
            // Check if this voxel has a block
            var chunkMgr = ChunkManager.Instance;
            if (chunkMgr != null)
            {
                Vector3 worldPos = new Vector3(voxel.X + 0.5f, voxel.Y + 0.5f, voxel.Z + 0.5f);
                var block = chunkMgr.GetBlockAtWorld(worldPos);
                
                if (block != null && block != BlockManager.Instance.Air)
                {
                    // Found a block!
                    if (chunkMgr.TryGetChunkAtWorld(worldPos, out _targetChunk, out var coord))
                    {
                        var local = chunkMgr.WorldToLocalBlock(worldPos, coord);
                        _targetBlock = local;
                        
                        // Calculate place position from last voxel
                        _placeBlockPos = chunkMgr.WorldToLocalBlock(
                            new Vector3(lastVoxel.X + 0.5f, lastVoxel.Y + 0.5f, lastVoxel.Z + 0.5f), 
                            coord
                        );
                        
                        UpdateHighlight(worldPos, _targetChunk);
                        return;
                    }
                }
            }
            
            // Step to next voxel
            lastVoxel = voxel;
            
            if (tMax.X < tMax.Y)
            {
                if (tMax.X < tMax.Z)
                {
                    t = tMax.X;
                    voxel.X += (int)step.X;
                    tMax.X += deltaDist.X;
                    lastNormal = new Vector3(-step.X, 0, 0);
                }
                else
                {
                    t = tMax.Z;
                    voxel.Z += (int)step.Z;
                    tMax.Z += deltaDist.Z;
                    lastNormal = new Vector3(0, 0, -step.Z);
                }
            }
            else
            {
                if (tMax.Y < tMax.Z)
                {
                    t = tMax.Y;
                    voxel.Y += (int)step.Y;
                    tMax.Y += deltaDist.Y;
                    lastNormal = new Vector3(0, -step.Y, 0);
                }
                else
                {
                    t = tMax.Z;
                    voxel.Z += (int)step.Z;
                    tMax.Z += deltaDist.Z;
                    lastNormal = new Vector3(0, 0, -step.Z);
                }
            }
            
            if (t > maxDistance)
                break;
        }
        
        // No hit
        if (BlockHighlight != null)
            BlockHighlight.Visible = false;
    }

    // Fallback physics raycast (slower but more reliable)
    private void UpdateBlockAimPhysics()
    {
        var from = Camera.GlobalPosition;
        var to = from + (-Camera.GlobalTransform.Basis.Z * MaxReach);

        _rayQueryParams.From = from;
        _rayQueryParams.To = to;

        var result = _spaceState.IntersectRay(_rayQueryParams);

        if (result.Count == 0)
        {
            if (BlockHighlight != null)
                BlockHighlight.Visible = false;
            return;
        }

        var hit = (Vector3)result["position"];
        var normal = (Vector3)result["normal"];
        var collider = ((Godot.Variant)result["collider"]).As<GodotObject>();

        ProcessRaycastHit(hit, normal, collider);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessRaycastHit(Vector3 hit, Vector3 normal, GodotObject collider)
    {
        // Find the Chunk in the scene tree
        Node node = collider as Node;
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

        const float epsilon = 0.001f;
        Vector3 localBreak = _targetChunk.ToLocal(hit - normal * epsilon);
        Vector3 localPlace = _targetChunk.ToLocal(hit + normal * epsilon);

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

        UpdateHighlight(hit, _targetChunk);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateHighlight(Vector3 worldPos, NeinCraft.Scene.Chunk.Chunk chunk)
    {
        if (BlockHighlight == null || !EnableBlockHighlight)
            return;

        bool show = chunk.InBounds(_placeBlockPos);
        BlockHighlight.Visible = show;

        if (show)
        {
            Vector3 center = chunk.ToGlobal(_placeBlockPos + new Vector3(0.5f, 0.5f, 0.5f));
            BlockHighlight.GlobalTransform = new Transform3D(Basis.Identity, center);
        }
    }

    private void TryBreakBlock()
    {
        if (_targetChunk == null)
            return;

        if (_targetChunk.SetBlockLocal(_targetBlock, BlockManager.Instance.Air))
        {
            _targetChunk.Rebuild();

            if (BreakSound != null)
            {
                BreakSound.GlobalPosition = _targetChunk.ToGlobal(_targetBlock + new Vector3(0.5f, 0.5f, 0.5f));
                BreakSound.PitchScale = 1.0f + (float)GD.RandRange(-0.1, 0.1);
                BreakSound.Play();
            }
        }
    }

    private void TryPlaceBlock()
    {
        if (_targetChunk == null)
            return;

        var block = BlockManager.Instance.Dirt;
        PlaceBlockSafe(_targetChunk, _placeBlockPos, block);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PlaceBlockSafe(NeinCraft.Scene.Chunk.Chunk chunk, Vector3I pos, Block block)
    {
        if (!chunk.InBounds(pos))
            return;

        var worldPos = chunk.ToGlobal(pos);
        
        // Reuse pre-allocated AABBs
        _blockAabb.Position = worldPos;
        _playerAabb.Position = GlobalPosition - PlayerHalfExtents;

        if (_playerAabb.Intersects(_blockAabb))
        {
            // Only allow placing below player (auto-jump)
            if (pos.Y < Mathf.FloorToInt(GlobalPosition.Y))
            {
                chunk.SetBlockLocal(pos, block);
                chunk.Rebuild();

                float topY = worldPos.Y + 1.0f;
                GlobalPosition = new Vector3(GlobalPosition.X, topY + 0.05f, GlobalPosition.Z);
                Velocity = Vector3.Zero;

                PlayPlaceSound(worldPos);
            }
        }
        else
        {
            chunk.SetBlockLocal(pos, block);
            chunk.Rebuild();
            PlayPlaceSound(worldPos);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PlayPlaceSound(Vector3 worldPos)
    {
        if (PlaceSound != null)
        {
            PlaceSound.GlobalPosition = worldPos + new Vector3(0.5f, 0.5f, 0.5f);
            PlaceSound.PitchScale = 1.0f + (float)GD.RandRange(-0.1, 0.1);
            PlaceSound.Play();
        }
    }
}
