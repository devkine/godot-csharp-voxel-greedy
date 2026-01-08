using Godot;
using System;
using System.Collections.Generic;

[Tool]
public partial class WireframeCube : MeshInstance3D
{
	[Export] public Vector3 Size = new Vector3(1, 1, 1);
	[Export] public Color WireColor = new Color(1, 1, 1); // orange
	[Export] public float LineWidth = 0.02f; // thickness in world units
	[Export] public float CornerRadius = 0.013f; // sphere radius
	[Export] public int CornerLod = 8; // number of segments, low-poly (default 3)

	private readonly List<MeshInstance3D> _edges = new();
	private readonly List<MeshInstance3D> _corners = new();

	public override void _Ready()
	{
		CreateWireframe();
	}

	private void CreateWireframe()
	{
		// Clear old edges
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

		Vector3 half = Size * 0.5f;

		// 8 cube vertices
		Vector3[] v = new Vector3[8];
		v[0] = new Vector3(-half.X, -half.Y, -half.Z);
		v[1] = new Vector3( half.X, -half.Y, -half.Z);
		v[2] = new Vector3( half.X,  half.Y, -half.Z);
		v[3] = new Vector3(-half.X,  half.Y, -half.Z);
		v[4] = new Vector3(-half.X, -half.Y,  half.Z);
		v[5] = new Vector3( half.X, -half.Y,  half.Z);
		v[6] = new Vector3( half.X,  half.Y,  half.Z);
		v[7] = new Vector3(-half.X,  half.Y,  half.Z);

		// 12 cube edges
		int[,] edges = {
			{0,1},{1,2},{2,3},{3,0},
			{4,5},{5,6},{6,7},{7,4},
			{0,4},{1,5},{2,6},{3,7}
		};

		// Create edges
		for (int i = 0; i < edges.GetLength(0); i++)
		{
			AddEdge(v[edges[i,0]], v[edges[i,1]]);
		}

		// Create corner spheres
		foreach (var pos in v)
		{
			AddCorner(pos);
		}
	}

	private void AddEdge(Vector3 start, Vector3 end)
	{
		var edge = new MeshInstance3D();
		var length = (end - start).Length();

		var mesh = new BoxMesh
		{
			Size = new Vector3(LineWidth, LineWidth, length)
		};

		var mat = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = WireColor
		};

		edge.Mesh = mesh;
		edge.MaterialOverride = mat;
		edge.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;

		// Place edge at midpoint
		edge.Position = (start + end) / 2.0f;

		// Orient edge along the line direction
		Vector3 dir = (end - start).Normalized();
		Vector3 up = Math.Abs(dir.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		Vector3 right = up.Cross(dir).Normalized();
		Vector3 newUp = dir.Cross(right).Normalized();

		edge.Basis = new Basis(right, newUp, dir);

		AddChild(edge);
		_edges.Add(edge);
	}

	private void AddCorner(Vector3 position)
	{
		var sphere = new MeshInstance3D();

		var mesh = new SphereMesh
		{
			Radius = CornerRadius,
			Height = CornerRadius * 2f,
			RadialSegments = CornerLod,
			Rings = CornerLod
		};

		var mat = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = WireColor
		};

		sphere.Mesh = mesh;
		sphere.MaterialOverride = mat;
		sphere.Position = position;
		sphere.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;


		AddChild(sphere);
		_corners.Add(sphere);
	}

	public override void _Process(double delta)
	{
		// Keep wireframe updated if edited in editor
		if (Engine.IsEditorHint())
		{
			CreateWireframe();
		}
	}
}
