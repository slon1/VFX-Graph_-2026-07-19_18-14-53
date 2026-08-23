using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class RepeatCountTests
{
    private const string Field = "density";
    private const string DiffuseCompute = "Assets/Shaders/GPU/Passes/DiffusePasses.compute";
    private const int Width = 32;
    private const int Height = 32;
    private const int CenterX = 16;
    private const int CenterY = 16;
    private const float Rate = 0.2f;
    private const float DeltaTime = 1f;

    /// <summary>
    /// Checks World/harness repeat semantics, not DiffuseFieldPass the type.
    /// DiffuseFieldPass is a sealed class; FieldKernelPass.Execute is already sealed.
    /// </summary>
    private class RepeatDiffuseStub : FieldKernelPass
    {
        private readonly string fieldName = Field;
        [NonSerialized] private FieldRequest[] fieldWritesCache;

        public int Iterations { get; set; } = 1;
        public float DiffusionRate { get; set; } = Rate;

        public override int RepeatCount => Iterations;
        public override string DisplayName => "Repeat Diffuse Stub";
        public override PassCategory Category => PassCategory.Transport;
        protected override string KernelName => "DiffuseField";

        public override IReadOnlyList<FieldRequest> FieldWrites =>
            FieldRequestSets.Single(
                ref fieldWritesCache, fieldName,
                FieldAccess.WritePingPong, FieldSemantic.Scalar, 1);

        protected override void SetParams(SimContext context, float deltaTime)
        {
            SetFloat(context, SimShaderIds.DeltaTime, deltaTime);
            SetFloat(context, SimShaderIds.DiffusionRate, DiffusionRate);
        }
    }

    private sealed class OneShotDiffuseStub : RepeatDiffuseStub
    {
        private bool hasFired;

        public override string DisplayName => "One-Shot Diffuse Stub";
        protected override bool ShouldDispatch => !hasFired;

        protected override void SetParams(SimContext context, float deltaTime)
        {
            base.SetParams(context, deltaTime);
            hasFired = true;
        }
    }

    private sealed class ZeroRepeatStub : SimPass
    {
        public int Count { get; set; }

        public override string DisplayName => "Zero Repeat Stub";
        public override PassCategory Category => PassCategory.Transport;
        public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
        public override IReadOnlyList<AttributeId> Writes => AttrSets.None;
        public override int RepeatCount => Count;
        public override void Initialize(SimContext context) { }
        public override void Execute(SimContext context, float deltaTime) { }
    }

    [Test]
    [Category("GPU")]
    public void RepeatCount6_MatchesSixSingleExecutions_Bitwise()
    {
        float[] seed = DeltaSeed();
        float[] repeated = RunWithRepeat(seed, iterations: 6);
        float[] sequential = RunExplicit(seed, repeat: 6);
        AssertBitwiseEqual(repeated, sequential, "RepeatCount=6 vs six Execute+Swap in one CB");
    }

    [Test]
    [Category("GPU")]
    public void RepeatCount_EvenAndOdd_ResultLivesInCurrent()
    {
        float[] seed = DeltaSeed();

        float[] evenRepeat = RunWithRepeat(seed, iterations: 2);
        float[] evenExplicit = RunExplicit(seed, repeat: 2);
        AssertBitwiseEqual(evenRepeat, evenExplicit, "RepeatCount=2 vs two singles");

        float[] oddRepeat = RunWithRepeat(seed, iterations: 3);
        float[] oddExplicit = RunExplicit(seed, repeat: 3);
        AssertBitwiseEqual(oddRepeat, oddExplicit, "RepeatCount=3 vs three singles");
    }

    [Test]
    [Category("GPU")]
    public void ShouldDispatch_InsideLoop_SwapsExactlyOnce()
    {
        float[] seed = DeltaSeed();

        using (FieldTestHarness harness = CreateHarness())
        {
            harness.SeedScalar(Field, seed);
            SimField field = harness.Context.Fields.Get(Field);
            RenderTexture oldCurrent = field.Current;
            RenderTexture oldNext = field.Next;

            OneShotDiffuseStub pass = new OneShotDiffuseStub { Iterations = 4 };
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);

            Assert.IsTrue(
                ReferenceEquals(field.Current, oldNext),
                "one swap: Current must be the RT that was Next before the loop");
            Assert.IsFalse(
                ReferenceEquals(field.Current, oldCurrent),
                "zero swaps would leave Current on the original RT");

            float[] obtained = harness.ReadScalar(Field);
            float[] oneIter = RunExplicit(seed, repeat: 1);
            AssertBitwiseEqual(
                obtained, oneIter,
                "one-shot RepeatCount=4 must match a single iteration (rules out 3 swaps)");
        }
    }

    [Test]
    public void AllConcreteSimPasses_DefaultRepeatCountIsOne()
    {
        Type[] types = typeof(SimPass).Assembly.GetTypes();
        int checkedCount = 0;
        for (int i = 0; i < types.Length; i++)
        {
            Type type = types[i];
            if (type.IsAbstract || !typeof(SimPass).IsAssignableFrom(type))
            {
                continue;
            }

            SimPass pass;
            try
            {
                pass = (SimPass)Activator.CreateInstance(type);
            }
            catch (Exception exception)
            {
                Assert.Fail(
                    $"Could not construct '{type.FullName}' to read RepeatCount: {exception.GetType().Name}: {exception.Message}");
                return;
            }

            Assert.AreEqual(1, pass.RepeatCount, type.Name);
            checkedCount++;
        }

        Assert.That(checkedCount, Is.GreaterThan(0), "expected concrete SimPass types in the runtime assembly");
        TestContext.WriteLine($"RepeatCount default 1 on {checkedCount} concrete SimPass types");
    }

    [Test]
    public void Validator_RepeatCountZero_ThrowsWithPassNameAndAdr()
    {
        ZeroRepeatStub pass = new ZeroRepeatStub { Count = 0 };
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => RepeatCountValidator.Validate(new SimPass[] { pass }));
        StringAssert.Contains(pass.DisplayName, ex.Message);
        StringAssert.Contains("ADR-015", ex.Message);
    }

    [Test]
    public void Validator_RepeatCountNegative_ThrowsWithPassNameAndAdr()
    {
        ZeroRepeatStub pass = new ZeroRepeatStub { Count = -1 };
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => RepeatCountValidator.Validate(new SimPass[] { pass }));
        StringAssert.Contains(pass.DisplayName, ex.Message);
        StringAssert.Contains("ADR-015", ex.Message);
    }

    [Test]
    public void Validator_DisabledPassWithZeroRepeat_IsSkipped()
    {
        ZeroRepeatStub pass = new ZeroRepeatStub { Count = 0 };
        pass.Enabled = false;
        Assert.DoesNotThrow(() => RepeatCountValidator.Validate(new SimPass[] { pass }));
    }

    private static float[] RunWithRepeat(float[] seed, int iterations)
    {
        using (FieldTestHarness harness = CreateHarness())
        {
            harness.SeedScalar(Field, seed);
            RepeatDiffuseStub pass = new RepeatDiffuseStub { Iterations = iterations };
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime);
            return harness.ReadScalar(Field);
        }
    }

    private static float[] RunExplicit(float[] seed, int repeat)
    {
        using (FieldTestHarness harness = CreateHarness())
        {
            harness.SeedScalar(Field, seed);
            RepeatDiffuseStub pass = new RepeatDiffuseStub { Iterations = 1 };
            pass.Initialize(harness.Context);
            harness.RunPass(pass, DeltaTime, repeat);
            return harness.ReadScalar(Field);
        }
    }

    private static FieldTestHarness CreateHarness()
    {
        FieldDescriptor desc = FieldTestHarness.Descriptor(
            Field,
            FieldSemantic.Scalar,
            GraphicsFormat.R32_SFloat,
            new Vector2Int(Width, Height),
            new Vector2(Width, Height),
            Color.clear);
        return new FieldTestHarness(new[] { desc }, DiffuseCompute);
    }

    private static float[] DeltaSeed()
    {
        float[] seed = new float[Width * Height];
        seed[CenterY * Width + CenterX] = 1f;
        return seed;
    }

    private static void AssertBitwiseEqual(float[] obtained, float[] expected, string message)
    {
        Assert.AreEqual(expected.Length, obtained.Length, $"{message}: length");
        int mismatches = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(obtained[i]) != BitConverter.SingleToInt32Bits(expected[i]))
            {
                mismatches++;
                Assert.AreEqual(
                    BitConverter.SingleToInt32Bits(expected[i]),
                    BitConverter.SingleToInt32Bits(obtained[i]),
                    $"{message}: [{i}] obtained={obtained[i]:G9} expected={expected[i]:G9}");
            }
        }

        Assert.AreEqual(0, mismatches, message);
    }
}
