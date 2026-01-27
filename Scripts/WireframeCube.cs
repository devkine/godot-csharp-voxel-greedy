using Godot;
using System;
using System.Collections.Generic;

[Tool]
public partial class WireframeCube : MeshInstance3D
{
	[Export] public Vector3 Size = new Vector3(1, 1, 1);
	[Export] public Color WireColor = new Color(1, 1, 1);
	[Export] public float LineWidth = 0.02f;
	[Export] public float CornerRadius = 0.013f;
	[Export] public int CornerLod = 6; // OPTIMIZATION: Reduced from 8 to 6

	private readonly List<MeshInstance3D> _edges = new(12); // OPTIMIZATION: Pre-sized
	private readonly List<MeshInstance3D> _corners = new(8); // OPTIMIZATION: Pre-sized

	// OPTIMIZATION: Cache material to avoid recreation
	private StandardMaterial3D _cachedMaterial;
	private Vector3 _lastSize;
	private Color _lastColor;
	private float _lastLineWidth;
	private float _lastCornerRadius;
	private int _lastCornerLod;

	public override void _Ready()
	{
		CreateWireframe();
	}

	private void CreateWireframe()
	{
		// OPTIMIZATION: Only rebuild if parameters changed
		if (_cachedMaterial != null &&
			_lastSize == Size &&
			_lastColor == WireColor &&
			Mathf.IsEqualApprox(_lastLineWidth, LineWidth) &&
			Mathf.IsEqualApprox(_lastCornerRadius, CornerRadius) &&
			_lastCornerLod == CornerLod)
		{
			return;
		}

		_lastSize = Size;
		_lastColor = WireColor;
		_lastLineWidth = LineWidth;
		_lastCornerRadius = CornerRadius;
		_lastCornerLod = CornerLod;

		// Clear old geometry
		foreach (var e in _edges)
		{
			if (IsInstanceValid(e))
				e.QueueFree();
		}
		_edges.Clear();

		foreach (var c in _corners)
		{
			if (IsInstanceValid(c))
				c.QueueFree();
		}
		_corners.Clear();

		// OPTIMIZATION: Create shared material once
		_cachedMaterial = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = WireColor,
			DisableReceiveShadows = true, // OPTIMIZATION: No shadows needed
			CullMode = BaseMaterial3D.CullModeEnum.Disabled // OPTIMIZATION: Visible from all angles
		};

		Vector3 half = Size * 0.5f;

		// OPTIMIZATION: Stack-allocated vertices
		Span<Vector3> v = stackalloc Vector3[8];
		v[0] = new Vector3(-half.X, -half.Y, -half.Z);
		v[1] = new Vector3( half.X, -half.Y, -half.Z);
		v[2] = new Vector3( half.X,  half.Y, -half.Z);
		v[3] = new Vector3(-half.X,  half.Y, -half.Z);
		v[4] = new Vector3(-half.X, -half.Y,  half.Z);
		v[5] = new Vector3( half.X, -half.Y,  half.Z);
		v[6] = new Vector3( half.X,  half.Y,  half.Z);
		v[7] = new Vector3(-half.X,  half.Y,  half.Z);

		// OPTIMIZATION: Pre-defined edge indices
		ReadOnlySpan<int> edgeIndices = stackalloc int[]
		{
			0,1, 1,2, 2,3, 3,0,  // Bottom face
			4,5, 5,6, 6,7, 7,4,  // Top face
			0,4, 1,5, 2,6, 3,7   // Vertical edges
		};

		// Create edges
		for (int i = 0; i < edgeIndices.Length; i += 2)
		{
			AddEdge(v[edgeIndices[i]], v[edgeIndices[i + 1]]);
		}

		// Create corners
		for (int i = 0; i < 8; i++)
		{
			AddCorner(v[i]);
		}
	}

	private void AddEdge(Vector3 start, Vector3 end)
	{
		var edge = new MeshInstance3D();
		var length = (end - start).Length();

		// OPTIMIZATION: Reuse mesh instances instead of creating new ones
		var mesh = new BoxMesh
		{
			Size = new Vector3(LineWidth, LineWidth, length)
		};

		edge.Mesh = mesh;
		edge.MaterialOverride = _cachedMaterial; // OPTIMIZATION: Shared material
		edge.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

		edge.Position = (start + end) * 0.5f;

		// OPTIMIZATION: Simplified orientation calculation
		Vector3 dir = (end - start).Normalized();
		Vector3 up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		Vector3 right = up.Cross(dir).Normalized();
		Vector3 newUp = dir.Cross(right).Normalized();

		edge.Basis = new Basis(right, newUp, dir);

		AddChild(edge);
		_edges.Add(edge);
	}

	private void AddCorner(Vector3 position)
	{
		var sphere = new MeshInstance3D();

		// OPTIMIZATION: Lower LOD for corners
		var mesh = new SphereMesh
		{
			Radius = CornerRadius,
			Height = CornerRadius * 2f,
			RadialSegments = CornerLod,
			Rings = CornerLod
		};

		sphere.Mesh = mesh;
		sphere.MaterialOverride = _cachedMaterial; // OPTIMIZATION: Shared material
		sphere.Position = position;
		sphere.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

		AddChild(sphere);
		_corners.Add(sphere);
	}

	public override void _Process(double delta)
	{
		// OPTIMIZATION: Only update in editor, not in game
		if (Engine.IsEditorHint())
		{
			CreateWireframe();
		}
	}
}
