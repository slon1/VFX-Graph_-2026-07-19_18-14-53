using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;
[System.Serializable]
public class BitmapSource : IDataSource {
	[SerializeField] private Texture2D texture;

	[SerializeField] private float targetWidth = 2f;
	[SerializeField] private float heightScale = 1f;
	public string Name => "Bitmap";

	public void Setup(PointDataset dataset) {
		Color32[] pixels = texture.GetPixels32();

		int width = texture.width;
		int height = texture.height;

		Vector3[] positions = new Vector3[pixels.Length];

		float pixelSize = targetWidth / Mathf.Max(width - 1, height - 1);

		float halfWidth = (width - 1) * 0.5f;
		float halfHeight = (height - 1) * 0.5f;

		int index = 0;

		for (int y = 0; y < height; y++) {
			for (int x = 0; x < width; x++) {
				Color32 c = pixels[index];

				float brightness =(	0.2126f * c.r +	0.7152f * c.g +	0.0722f * c.b) / 255f;

				positions[index] = new Vector3(
					(x - halfWidth) * pixelSize,
					brightness * heightScale,
					(y - halfHeight) * pixelSize);

				index++;
			}
		}

		dataset.EnsureCapacity(positions.Length);

		GraphicsBuffer positionBuffer =
			dataset.RegisterAttribute(BuiltinAttributes.Position);

		positionBuffer.SetData(positions);
	}

	public void Tick(PointDataset dataset) {

	}
}
