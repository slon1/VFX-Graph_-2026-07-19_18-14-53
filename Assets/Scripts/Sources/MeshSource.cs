using System;
using UnityEngine;
[System.Serializable]
public class MeshSource : IDataSource
{
    [SerializeField] private Mesh mesh;
	[SerializeField] private bool centerPivot = true;
	[SerializeField] private bool normalizeScale = true;
	[SerializeField] private float targetSize = 2f;

	public string Name => "Mesh";

	public void Setup(PointDataset dataset) {
		if (mesh == null) {
			throw new InvalidOperationException("Mesh is not assigned.");
		}

		Vector3[] vertices = mesh.vertices;

		if (centerPivot) {
			Vector3 center = mesh.bounds.center;

			for (int i = 0; i < vertices.Length; i++) {
				vertices[i] -= center;
			}
		}
		if (normalizeScale) {
			float maxSize = Mathf.Max(
				mesh.bounds.size.x,
				mesh.bounds.size.y,
				mesh.bounds.size.z);

			float scale = targetSize / maxSize;

			for (int i = 0; i < vertices.Length; i++) {
				vertices[i] *= scale;
			}
		}



		dataset.EnsureCapacity(vertices.Length);
		GraphicsBuffer positions = dataset.RegisterAttribute(BuiltinAttributes.Position);
		positions.SetData(vertices);
	}

	public void Tick(PointDataset dataset) {
		
	}
}
