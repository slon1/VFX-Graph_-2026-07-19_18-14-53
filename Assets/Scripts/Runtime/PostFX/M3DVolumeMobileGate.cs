using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Desktop-first post-processing: disables the scene <see cref="Volume"/> on mobile
/// so Bloom/ACES do not run until a separate GPU budget measurement exists (ADR-025).
/// </summary>
[RequireComponent(typeof(Volume))]
public sealed class M3DVolumeMobileGate : MonoBehaviour
{
    private void Awake()
    {
        if (Application.isMobilePlatform)
        {
            GetComponent<Volume>().enabled = false;
        }
    }
}
