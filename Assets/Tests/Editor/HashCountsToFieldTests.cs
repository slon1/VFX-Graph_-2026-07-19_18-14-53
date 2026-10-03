using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[TestFixture]
public class HashCountsToFieldTests
{
    private const string HashPath = "Assets/Shaders/GPU/Passes/SpatialHashPasses.compute";
    private const string DebugPath = "Assets/Shaders/GPU/Passes/HashDebugPasses.compute";

    // R16_SFloat stores integers exactly through 2048. 2000 leaves a margin of 48.
    // A larger particle count needs a wider field format.
    private const int ParticleCount = 2000;
    private const float CellMargin = 0.01f;

    private static readonly Vector3 Extents = new Vector3(16f, 0f, 16f);
    private const float MinCellSize = 2f;

    private ParticleSet particles;
    private FieldSet fields;
    private CommandBuffer cmd;
    private ComputeShader hashShader;
    private ComputeShader debugShader;
    private BuildSpatialHashPass builder;
    private HashCountsToFieldPass counts;
    private SimContext context;

    [SetUp]
    public void SetUp()
    {
        Assume.That(SystemInfo.supportsComputeShaders);
        hashShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(HashPath);
        debugShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(DebugPath);
        Assume.That(hashShader != null, "SpatialHashPasses.compute must be imported.");
        Assume.That(debugShader != null, "HashDebugPasses.compute must be imported.");
        particles = new ParticleSet();
        fields = new FieldSet();
        cmd = new CommandBuffer { name = "HashCountsToField" };
    }

    [TearDown]
    public void TearDown()
    {
        builder?.Dispose();
        builder = null;
        counts = null;
        context = null;
        cmd?.Release();
        cmd = null;
        particles?.Dispose();
        particles = null;
        fields?.Dispose();
        fields = null;
    }

    [Test]
    public void Histogram_MatchesCpu_AndSumsToParticleCount()
    {
        int texturesBefore = CountHashCountTextures();
        SpatialHashLayout layout = Grid();
        Vector3[] positions = RandomPositions(layout, ParticleCount, 1);
        int[] actual = Dispatch(layout, positions, matchField: true);

        int[] expected = CpuHistogram(positions, layout);
        Assert.AreEqual(expected.Length, actual.Length);
        int sum = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.AreEqual(expected[i], actual[i], "Cell " + i);
            sum += actual[i];
        }

        Assert.AreEqual(ParticleCount, sum);
        ReleaseFields();
        Assert.AreEqual(
            texturesBefore,
            CountHashCountTextures(),
            "M3D_hashCount_: было " + texturesBefore + ", стало " + CountHashCountTextures() + ".");
    }

    [Test]
    public void SecondDispatch_OverwritesPreviousHistogram()
    {
        SpatialHashLayout layout = Grid();
        Vector3[] first = RandomPositions(layout, ParticleCount, 1);
        int[] firstField = Dispatch(layout, first, matchField: true);

        Vector3[] second = ShiftByOneCell(first, layout);
        particles.Get(BuiltinAttributes.Position).SetData(second);
        builder.Execute(context, 0f);
        counts.Execute(context, 0f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        int[] secondField = ReadField(fields.Get("hashCount"), layout.Resolution);
        int[] expected = CpuHistogram(second, layout);
        bool changed = false;
        for (int i = 0; i < secondField.Length; i++)
        {
            Assert.AreEqual(expected[i], secondField[i], "Cell " + i);
            if (secondField[i] != firstField[i])
            {
                changed = true;
            }
        }

        Assert.IsTrue(changed, "Second histogram still matches the first positions.");
        ReleaseFields();
    }

    [Test]
    public void ResolutionMismatch_ThrowsWithFieldNameAndBothResolutions()
    {
        SpatialHashLayout layout = Grid();
        Vector3[] positions = RandomPositions(layout, 8, 1);
        PrepareParticles(positions);
        FieldDescriptor descriptor = MatchingField(layout);
        SetPrivate(descriptor, "resolution", layout.Resolution + new Vector2Int(1, 0));
        Allocate(descriptor);
        Rebind();
        BuildHash();

        counts = new HashCountsToFieldPass();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => counts.Initialize(context));
        string message = exception.Message;
        StringAssert.Contains("hashCount", message);
        StringAssert.Contains(layout.Resolution.x + "x" + layout.Resolution.y, message);
        StringAssert.Contains((layout.Resolution.x + 1) + "x" + layout.Resolution.y, message);
        ReleaseFields();
    }

    [Test]
    public void SizeMismatch_ThrowsBeyondTolerance_ExactTolerancePasses()
    {
        SpatialHashLayout layout = Grid();
        Vector3[] positions = RandomPositions(layout, 8, 1);
        PrepareParticles(positions);

        FieldDescriptor onThreshold = MatchingField(layout);
        Vector2 allowed = layout.Size;
        float bump = 1e-3f;
        allowed.x += bump;
        while (Mathf.Abs(allowed.x - layout.Size.x) > 1e-3f)
        {
            bump *= 0.5f;
            allowed = layout.Size;
            allowed.x += bump;
        }

        SetPrivate(onThreshold, "size", allowed);
        Allocate(onThreshold);
        Rebind();
        BuildHash();
        counts = new HashCountsToFieldPass();
        Assert.DoesNotThrow(() => counts.Initialize(context));
        ReleaseFields();

        FieldDescriptor tooFar = MatchingField(layout);
        Vector2 rejected = layout.Size;
        rejected.x += 0.01f;
        Assert.Greater(Mathf.Abs(rejected.x - layout.Size.x), 1e-3f);
        SetPrivate(tooFar, "size", rejected);
        Allocate(tooFar);
        Rebind();
        BuildHash();
        counts = new HashCountsToFieldPass();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => counts.Initialize(context));
        StringAssert.Contains("hashCount", exception.Message);
        StringAssert.Contains(layout.Resolution.x + "x" + layout.Resolution.y, exception.Message);
        ReleaseFields();
    }

    [Test]
    public void Initialize_WithoutSpatialHash_Throws()
    {
        counts = new HashCountsToFieldPass();
        Rebind();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => counts.Initialize(context));
        StringAssert.Contains("hash", exception.Message);
    }

    [Test]
    public void EnabledPassWithoutBuilder_FailsExistingValidator()
    {
        var passes = new List<SimPass> { new HashCountsToFieldPass() };
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SpatialHashValidator.Validate(passes, null));
        StringAssert.Contains("Build Spatial Hash", exception.Message);
    }

    [Test]
    public void Contract_KernelNameAndDefaultField()
    {
        HashCountsToFieldPass pass = new HashCountsToFieldPass();
        Assert.AreEqual("hashCount", pass.FieldName);
        Assert.AreEqual(PassCategory.Emit, pass.Category);

        PropertyInfo kernelName = typeof(FieldKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("HashCountsToField", (string)kernelName.GetValue(pass));
        Assert.GreaterOrEqual(debugShader.FindKernel("HashCountsToField"), 0);
    }

    private int[] Dispatch(SpatialHashLayout layout, Vector3[] positions, bool matchField)
    {
        PrepareParticles(positions);
        FieldDescriptor descriptor = MatchingField(layout);
        if (!matchField)
        {
            SetPrivate(descriptor, "resolution", layout.Resolution + new Vector2Int(1, 0));
        }

        Allocate(descriptor);
        Rebind();
        BuildHash();
        counts = new HashCountsToFieldPass();
        counts.Initialize(context);
        builder.Execute(context, 0f);
        counts.Execute(context, 0f);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        return ReadField(fields.Get(descriptor.Name), descriptor.Resolution);
    }

    private void PrepareParticles(Vector3[] positions)
    {
        int count = positions.Length;
        particles.EnsureCapacity(count);
        particles.RegisterAttribute(BuiltinAttributes.Position);
        particles.RegisterAttribute(BuiltinAttributes.Heading);
        particles.RegisterAttribute(BuiltinAttributes.TeamId);

        Vector3[] headings = new Vector3[count];
        uint[] teams = new uint[count];
        for (int i = 0; i < count; i++)
        {
            headings[i] = Vector3.right;
        }

        particles.Get(BuiltinAttributes.Position).SetData(positions);
        particles.Get(BuiltinAttributes.Heading).SetData(headings);
        particles.Get(BuiltinAttributes.TeamId).SetData(teams);
    }

    private void Allocate(FieldDescriptor descriptor)
    {
        fields.Dispose();
        fields = new FieldSet();
        fields.Allocate(new[] { descriptor }, cmd);
    }

    private void BuildHash()
    {
        builder?.Dispose();
        builder = new BuildSpatialHashPass
        {
            Center = Vector3.zero,
            Extents = Extents,
            MinCellSize = MinCellSize,
            Wrap = true,
        };
        builder.Initialize(context);
    }

    private void Rebind()
    {
        context = new SimContext(particles, fields, new[] { hashShader, debugShader }, null);
        context.Cmd = cmd;
    }

    private void ReleaseFields()
    {
        fields?.Dispose();
        fields = new FieldSet();
    }

    private static SpatialHashLayout Grid()
    {
        return SpatialHashSet.ComputeLayout(Vector3.zero, Extents, MinCellSize);
    }

    private static FieldDescriptor MatchingField(SpatialHashLayout layout)
    {
        FieldDescriptor descriptor = FieldDescriptor.CreateDefault("hashCount", FieldSemantic.Scalar);
        SetPrivate(descriptor, "resolution", layout.Resolution);
        SetPrivate(descriptor, "size", layout.Size);
        return descriptor;
    }

    private static void SetPrivate(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, name);
        field.SetValue(target, value);
    }

    private static int[] ReadField(SimField field, Vector2Int resolution)
    {
        AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(field.Current);
        request.WaitForCompletion();
        Assert.IsFalse(request.hasError, "Field readback failed.");
        NativeArray<ushort> raw = request.GetData<ushort>();
        int[] counts = new int[resolution.x * resolution.y];
        for (int y = 0; y < resolution.y; y++)
        {
            for (int x = 0; x < resolution.x; x++)
            {
                float value = Mathf.HalfToFloat(raw[y * resolution.x + x]);
                counts[y * resolution.x + x] = Mathf.RoundToInt(value);
            }
        }

        return counts;
    }

    private static int[] CpuHistogram(Vector3[] positions, SpatialHashLayout layout)
    {
        int[] counts = new int[layout.CellCount];
        for (int i = 0; i < positions.Length; i++)
        {
            counts[CpuCell(positions[i], layout, true)]++;
        }

        return counts;
    }

    private static Vector3[] ShiftByOneCell(Vector3[] positions, SpatialHashLayout layout)
    {
        Vector3[] shifted = new Vector3[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            shifted[i] = positions[i] + new Vector3(layout.CellSize.x, 0f, 0f);
        }

        return shifted;
    }

    private static Vector3[] RandomPositions(SpatialHashLayout layout, int count, int seed)
    {
        System.Random rng = new System.Random(seed);
        Vector3[] positions = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector3(
                Interior(rng, layout.Origin.x, layout.CellSize.x, layout.Resolution.x),
                0f,
                Interior(rng, layout.Origin.y, layout.CellSize.y, layout.Resolution.y));
        }

        return positions;
    }

    private static float Interior(System.Random rng, float origin, float cell, int resolution)
    {
        int c = rng.Next(resolution);
        float lo = origin + c * cell + CellMargin;
        float hi = origin + (c + 1f) * cell - CellMargin;
        return lo + (float)rng.NextDouble() * (hi - lo);
    }

    private static int CpuCell(Vector3 position, SpatialHashLayout layout, bool wrap)
    {
        int x = Axis(position.x, layout.Origin.x, layout.CellSize.x, layout.Resolution.x, wrap);
        int z = Axis(position.z, layout.Origin.y, layout.CellSize.y, layout.Resolution.y, wrap);
        int index = z * layout.Resolution.x + x;
        int last = layout.CellCount - 1;
        if (index > last)
        {
            index = last;
        }

        return index;
    }

    private static int Axis(float value, float origin, float cell, int resolution, bool wrap)
    {
        int c = Mathf.FloorToInt((value - origin) / cell);
        if (!wrap)
        {
            if (c < 0)
            {
                return 0;
            }

            return c > resolution - 1 ? resolution - 1 : c;
        }

        int wrapped = c % resolution;
        if (wrapped < 0)
        {
            wrapped += resolution;
        }

        return wrapped;
    }

    private static int CountHashCountTextures()
    {
        int count = 0;
        RenderTexture[] textures = Resources.FindObjectsOfTypeAll<RenderTexture>();
        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i] != null &&
                textures[i].name != null &&
                textures[i].name.StartsWith("M3D_hashCount_"))
            {
                count++;
            }
        }

        return count;
    }
}
