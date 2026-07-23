using System;
using UnityEngine;

[System.Serializable]
public sealed class BitmapSource : IDataSource
{
    [SerializeField] private Texture2D texture;
    [SerializeField] private float targetWidth = 2f;
    [SerializeField] private float heightScale = 1f;

    public string Name => "Bitmap";

    public void Setup(ParticleSet particles)
    {
        if (texture == null)
        {
            throw new InvalidOperationException("BitmapSource: texture is not assigned.");
        }

        if (!texture.isReadable)
        {
            throw new InvalidOperationException(
                $"BitmapSource: texture '{texture.name}' must have Read/Write Enabled in import settings.");
        }

        Color32[] pixels = texture.GetPixels32();
        int width = texture.width;
        int height = texture.height;

        if (width < 1 || height < 1 || pixels.Length == 0)
        {
            throw new InvalidOperationException("BitmapSource: texture has no pixels.");
        }

        int span = Mathf.Max(width - 1, height - 1);
        if (span <= 0)
        {
            throw new InvalidOperationException(
                "BitmapSource: texture must be at least 2px on the longer axis.");
        }

        float pixelSize = targetWidth / span;
        float halfWidth = (width - 1) * 0.5f;
        float halfHeight = (height - 1) * 0.5f;

        Vector3[] positions = new Vector3[pixels.Length];
        int index = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Color32 c = pixels[index];
                float brightness = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;

                positions[index] = new Vector3(
                    (x - halfWidth) * pixelSize,
                    brightness * heightScale,
                    (y - halfHeight) * pixelSize);

                index++;
            }
        }

        particles.EnsureCapacity(positions.Length);
        GraphicsBuffer restPositionBuffer = particles.RegisterAttribute(BuiltinAttributes.RestPosition);
        restPositionBuffer.SetData(positions);
    }

    public void Tick(ParticleSet particles)
    {
    }
}
