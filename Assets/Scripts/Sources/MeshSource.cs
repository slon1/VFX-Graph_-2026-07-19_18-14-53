using System;
using UnityEngine;

[System.Serializable]
public sealed class MeshSource : IDataSource
{
    [SerializeField] private Mesh mesh;
    [SerializeField] private bool centerPivot = true;
    [SerializeField] private bool normalizeScale = true;
    [SerializeField] private float targetSize = 2f;

    public string Name => "Mesh";

    public void Setup(ParticleSet particles)
    {
        if (mesh == null)
        {
            throw new InvalidOperationException("MeshSource: mesh is not assigned.");
        }

        if (!mesh.isReadable)
        {
            throw new InvalidOperationException(
                $"MeshSource: mesh '{mesh.name}' must have Read/Write Enabled in import settings.");
        }

        Vector3[] vertices = mesh.vertices;
        if (vertices == null || vertices.Length == 0)
        {
            throw new InvalidOperationException($"MeshSource: mesh '{mesh.name}' has no vertices.");
        }

        if (centerPivot)
        {
            Vector3 center = mesh.bounds.center;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] -= center;
            }
        }

        if (normalizeScale)
        {
            float maxSize = Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.y, mesh.bounds.size.z);
            if (maxSize <= 0f)
            {
                throw new InvalidOperationException(
                    $"MeshSource: mesh '{mesh.name}' has degenerate bounds (size is zero).");
            }

            float scale = targetSize / maxSize;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] *= scale;
            }
        }

        particles.EnsureCapacity(vertices.Length);
        GraphicsBuffer restPositions = particles.RegisterAttribute(BuiltinAttributes.RestPosition);
        restPositions.SetData(vertices);
    }

    public void Tick(ParticleSet particles)
    {
    }
}
