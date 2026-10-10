using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Own copy of the A2 stage. The A2 file stays untouched.
[TestFixture]
public class PrimitiveParticleBinderDirectionStageTests
{
    private const string ComputePath = "Assets/Tests/Editor/ParticlePaletteAddressing.compute";
    private const int RtSize = 256;
    private const float OrthoSize = 4f;
    private const float QuadSize = 1.6f;
    private const float StageTolerance = 2f / 255f;
    private const float UniformTolerance = 1f / 255f;

    private static readonly Vector3[] Positions =
    {
        new Vector3(-2.2f, 1.6f, 0f),
        new Vector3(2.2f, 1.6f, 0f),
        new Vector3(0f, -1.8f, 0f),
    };

    private static readonly Vector3[] Velocities =
    {
        new Vector3(0f, 0f, 1f),
        new Vector3(0f, 0f, -1f),
        new Vector3(1f, 0f, 0f),
    };

    private static readonly float[] Values = { 1f, 0f, 0.5f };
    private static readonly uint[] Teams = { 0u, 1u, 0u };

    [Test]
    public void QuadCentersMatchCompute_CornersMatchCenter()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        Assert.That(Shader.Find("M3D/ParticleBillboard") != null);
        ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        Assert.That(compute != null);

        ParticleSet particles = null;
        FieldSet fields = null;
        PrimitiveParticleBinder binder = null;
        GameObject cameraObject = null;
        RenderTexture rt = null;
        Texture2D readback = null;
        try
        {
            particles = new ParticleSet();
            particles.EnsureCapacity(Positions.Length);
            particles.RegisterAttribute(BuiltinAttributes.Position);
            particles.RegisterAttribute(BuiltinAttributes.Velocity);
            particles.RegisterAttribute(BuiltinAttributes.TeamId);
            Assert.AreEqual(12, particles.Get(BuiltinAttributes.Velocity).stride);
            particles.Get(BuiltinAttributes.Position).SetData(Positions);
            particles.Get(BuiltinAttributes.Velocity).SetData(Velocities);
            particles.Get(BuiltinAttributes.TeamId).SetData(Teams);
            fields = new FieldSet();

            Gradient[] rows =
            {
                Ramp(new Color(1f, 0f, 0f, 1f), new Color(0f, 0.15f, 1f, 1f)),
                Ramp(new Color(0.98f, 1f, 0f, 1f), new Color(0.02f, 1f, 0f, 1f)),
            };
            binder = new PrimitiveParticleBinder(
                QuadSize, Color.white, null, 4f, TeamsOf(rows), ParticleColorSource.Direction, new Vector3(0f, 0f, 1f));
            binder.Initialize(new SimContext(particles, fields, System.Array.Empty<ComputeShader>(), null));
            Assert.IsTrue(binder.UsesDirectionColor);
            Texture2D lut = TeamLut(binder);
            Vector4[] expected = Sample(compute, lut);

            rt = new RenderTexture(RtSize, RtSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                antiAliasing = 1,
                useMipMap = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();

            cameraObject = new GameObject("DirectionStageCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = OrthoSize;
            camera.aspect = 1f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 1f);
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 50f;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.transform.rotation = Quaternion.identity;
            camera.targetTexture = rt;
            camera.cullingMask = ~0;
            camera.useOcclusionCulling = false;

            UniversalAdditionalCameraData extra = camera.GetUniversalAdditionalCameraData();
            extra.renderPostProcessing = false;
            extra.antialiasing = AntialiasingMode.None;
            extra.renderShadows = false;
            extra.volumeLayerMask = 0;
            extra.requiresColorOption = CameraOverrideOption.Off;
            extra.requiresDepthOption = CameraOverrideOption.Off;

            AssignCamera(binder, camera);
            binder.Execute(new SimContext(particles, fields, System.Array.Empty<ComputeShader>(), null));
            camera.Render();
            readback = Read(rt);
            CheckQuads(camera, readback, expected);
        }
        finally
        {
            if (readback != null)
            {
                Object.DestroyImmediate(readback);
            }

            if (cameraObject != null)
            {
                Object.DestroyImmediate(cameraObject);
            }

            if (rt != null)
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }

            binder?.Dispose();
            particles?.Dispose();
            fields?.Dispose();
        }
    }

    private static void CheckQuads(Camera camera, Texture2D readback, Vector4[] expected)
    {
        for (int i = 0; i < Positions.Length; i++)
        {
            Vector2Int center = Pixel(camera, Positions[i]);
            Color centerColor = readback.GetPixel(center.x, center.y);
            AssertColor(expected[i], centerColor, StageTolerance, "center " + i);

            Vector2Int[] inside =
            {
                Pixel(camera, Positions[i] + new Vector3(QuadSize * 0.35f, QuadSize * 0.35f, 0f)),
                Pixel(camera, Positions[i] + new Vector3(QuadSize * 0.35f, -QuadSize * 0.35f, 0f)),
                Pixel(camera, Positions[i] + new Vector3(-QuadSize * 0.35f, QuadSize * 0.35f, 0f)),
            };
            for (int k = 0; k < inside.Length; k++)
            {
                Assert.AreNotEqual(center, inside[k]);
                Color corner = readback.GetPixel(inside[k].x, inside[k].y);
                AssertColor(centerColor, corner, UniformTolerance, "corner " + i + "/" + k);
            }
        }
    }

    private static Vector4[] Sample(ComputeShader shader, Texture2D lut)
    {
        int count = Values.Length;
        int kernel = shader.FindKernel("PaletteAddressing");
        var useLut = new float[count];
        var useTeams = new float[count];
        var teamCount = new float[count];
        var scale = new float[count];
        var color = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            useLut[i] = 1f;
            useTeams[i] = 1f;
            teamCount[i] = 2f;
            scale[i] = 1f;
            color[i] = new Vector4(1f, 1f, 1f, 1f);
        }

        GraphicsBuffer valueBuf = Structured(count, 4, Values);
        GraphicsBuffer teamBuf = Structured(count, 4, Teams);
        GraphicsBuffer useLutBuf = Structured(count, 4, useLut);
        GraphicsBuffer useTeamsBuf = Structured(count, 4, useTeams);
        GraphicsBuffer teamCountBuf = Structured(count, 4, teamCount);
        GraphicsBuffer scaleBuf = Structured(count, 4, scale);
        GraphicsBuffer colorBuf = Structured(count, 16, color);
        GraphicsBuffer outBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 16);
        try
        {
            shader.SetTexture(kernel, "_LutTex", lut);
            shader.SetBuffer(kernel, "_InValue", valueBuf);
            shader.SetBuffer(kernel, "_InTeam", teamBuf);
            shader.SetBuffer(kernel, "_InUseLut", useLutBuf);
            shader.SetBuffer(kernel, "_InUseTeams", useTeamsBuf);
            shader.SetBuffer(kernel, "_InTeamCount", teamCountBuf);
            shader.SetBuffer(kernel, "_InScale", scaleBuf);
            shader.SetBuffer(kernel, "_InColor", colorBuf);
            shader.SetBuffer(kernel, "_OutColor", outBuf);
            shader.SetInt("_CaseCount", count);
            shader.Dispatch(kernel, 1, 1, 1);
            var output = new Vector4[count];
            outBuf.GetData(output);
            return output;
        }
        finally
        {
            valueBuf.Release();
            teamBuf.Release();
            useLutBuf.Release();
            useTeamsBuf.Release();
            teamCountBuf.Release();
            scaleBuf.Release();
            colorBuf.Release();
            outBuf.Release();
        }
    }

    private static GraphicsBuffer Structured<T>(int count, int stride, T[] data) where T : struct
    {
        var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        buffer.SetData(data);
        return buffer;
    }

    private static void AssignCamera(PrimitiveParticleBinder binder, Camera camera)
    {
        FieldInfo field = typeof(PrimitiveParticleBinder).GetField(
            "renderParams", BindingFlags.Instance | BindingFlags.NonPublic);
        RenderParams renderParams = (RenderParams)field.GetValue(binder);
        renderParams.camera = camera;
        field.SetValue(binder, renderParams);
    }

    private static Texture2D TeamLut(PrimitiveParticleBinder binder)
    {
        FieldInfo field = typeof(PrimitiveParticleBinder).GetField(
            "teamLut", BindingFlags.Instance | BindingFlags.NonPublic);
        return (Texture2D)field.GetValue(binder);
    }

    private static Texture2D Read(RenderTexture rt)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D image = new Texture2D(RtSize, RtSize, TextureFormat.RGBA32, false, true)
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        image.ReadPixels(new Rect(0f, 0f, RtSize, RtSize), 0, 0);
        image.Apply(false);
        RenderTexture.active = previous;
        return image;
    }

    private static Vector2Int Pixel(Camera camera, Vector3 world)
    {
        Vector3 screen = camera.WorldToScreenPoint(world);
        int x = Mathf.Clamp(Mathf.RoundToInt(screen.x), 0, RtSize - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(screen.y), 0, RtSize - 1);
        return new Vector2Int(x, y);
    }

    private static TeamProfile[] TeamsOf(Gradient[] rows)
    {
        TeamProfile[] teams = new TeamProfile[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            teams[i] = new TeamProfile { Color = rows[i] };
        }

        return teams;
    }

    private static Gradient Ramp(Color a, Color b)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return gradient;
    }

    private static void AssertColor(Vector4 expected, Color actual, float tolerance, string label)
    {
        Assert.LessOrEqual(Mathf.Abs(expected.x - actual.r), tolerance, label + " r");
        Assert.LessOrEqual(Mathf.Abs(expected.y - actual.g), tolerance, label + " g");
        Assert.LessOrEqual(Mathf.Abs(expected.z - actual.b), tolerance, label + " b");
        Assert.LessOrEqual(Mathf.Abs(expected.w - actual.a), tolerance, label + " a");
    }

    private static void AssertColor(Color expected, Color actual, float tolerance, string label)
    {
        AssertColor(new Vector4(expected.r, expected.g, expected.b, expected.a), actual, tolerance, label);
    }
}
