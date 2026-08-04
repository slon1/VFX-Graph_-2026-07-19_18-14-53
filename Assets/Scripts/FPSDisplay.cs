using UnityEngine;

/// <summary>
/// Minimal on-screen FPS / frame-time overlay for device builds.
/// OnGUI-based (no Canvas/UI setup needed) — attach to any GameObject in the scene.
/// Updates the displayed text once every <see cref="updateInterval"/> seconds,
/// not every frame, so it doesn't add per-frame string-allocation noise to the profile.
/// </summary>
public sealed class FPSDisplay : MonoBehaviour {
	[SerializeField] private float updateInterval = 0.5f;
	[SerializeField] private int fontSize = 32;
	[SerializeField] private Color textColor = Color.green;

	private float accumTime;
	private int accumFrames;
	private float fps;
	private float frameMs;
	private string display = "";

	private GUIStyle style;

	private void Start() {
		Application.targetFrameRate = 120;
	}
	private void Update() {
		accumTime += Time.unscaledDeltaTime;
		accumFrames++;

		if (accumTime >= updateInterval) {
			fps = accumFrames / accumTime;
			frameMs = (accumTime / accumFrames) * 1000f;
			display = $"{fps:F0} FPS\n{frameMs:F1} ms";

			accumTime = 0f;
			accumFrames = 0;
		}
	}

	private void OnGUI() {
		if (style == null) {
			style = new GUIStyle(GUI.skin.label) {
				fontSize = fontSize,
				normal = { textColor = textColor }
			};
		}

		// Top-left corner, scaled by screen DPI-ish size so it's readable on phones too.
		GUI.Label(new Rect(20, 20, 400, 120), display, style);
	}
}