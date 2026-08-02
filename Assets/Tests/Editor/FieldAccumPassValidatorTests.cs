using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class FieldAccumPassValidatorTests
{
    private sealed class StubClearPass : SimPass
    {
        private readonly FieldAccumClearRequest[] clears;
        public StubClearPass(string field, int channels, bool enabled = true)
        {
            clears = new[] { new FieldAccumClearRequest(field, channels) };
            Enabled = enabled;
        }

        public override string DisplayName => "StubClear";
        public override PassCategory Category => PassCategory.Emit;
        public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
        public override IReadOnlyList<AttributeId> Writes => AttrSets.None;
        public override IReadOnlyList<FieldAccumClearRequest> FieldAccumClears => clears;
        public override void Initialize(SimContext context) { }
        public override void Execute(SimContext context, float deltaTime) { }
    }

    private sealed class StubScatterPass : SimPass
    {
        private readonly FieldAccumRequest[] writes;
        public StubScatterPass(string field, int channels, float scale, float bias, bool enabled = true)
        {
            writes = new[] { new FieldAccumRequest(field, channels, scale, bias) };
            Enabled = enabled;
        }

        public override string DisplayName => "StubScatter";
        public override PassCategory Category => PassCategory.Emit;
        public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
        public override IReadOnlyList<AttributeId> Writes => AttrSets.None;
        public override IReadOnlyList<FieldAccumRequest> FieldAccumWrites => writes;
        public override void Initialize(SimContext context) { }
        public override void Execute(SimContext context, float deltaTime) { }
    }

    private sealed class StubNormalizePass : SimPass
    {
        private readonly FieldAccumRequest[] reads;
        public StubNormalizePass(string field, int channels, float scale, float bias)
        {
            reads = new[] { new FieldAccumRequest(field, channels, scale, bias) };
        }

        public override string DisplayName => "StubNormalize";
        public override PassCategory Category => PassCategory.Emit;
        public override IReadOnlyList<AttributeId> Reads => AttrSets.None;
        public override IReadOnlyList<AttributeId> Writes => AttrSets.None;
        public override IReadOnlyList<FieldAccumRequest> FieldAccumReads => reads;
        public override void Initialize(SimContext context) { }
        public override void Execute(SimContext context, float deltaTime) { }
    }

    private static FieldDescriptor Rg16(string name) =>
        FieldDescriptor.CreateDefault(name, FieldSemantic.Velocity);

    private static System.Func<string, FieldDescriptor> DescriptorMap(params FieldDescriptor[] descriptors)
    {
        Dictionary<string, FieldDescriptor> map = new Dictionary<string, FieldDescriptor>();
        for (int i = 0; i < descriptors.Length; i++)
        {
            map[descriptors[i].Name] = descriptors[i];
        }

        return name => map.TryGetValue(name, out FieldDescriptor d) ? d : null;
    }

    [Test]
    public void BufferCount_IsChannelsPlusOne()
    {
        using (FieldAccumBuffer buffer = new FieldAccumBuffer(new Vector2Int(4, 4), valueChannels: 2))
        {
            Assert.That(buffer.Channels, Is.EqualTo(2));
            Assert.That(buffer.BufferCount, Is.EqualTo(3));
            Assert.That(buffer.ElementCount, Is.EqualTo(4 * 4 * 3));
        }
    }

    [Test]
    public void StateMachine_ClearScatterScatterNormalize_Succeeds()
    {
        FieldDescriptor field = Rg16("agentVelocity");
        SimPass[] passes =
        {
            new StubClearPass("agentVelocity", 2),
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
            new StubNormalizePass("agentVelocity", 2, 4096f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void StateMachine_NormalizeThenScatterWithoutClear_Fails()
    {
        FieldDescriptor field = Rg16("agentVelocity");
        SimPass[] passes =
        {
            new StubClearPass("agentVelocity", 2),
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
            new StubNormalizePass("agentVelocity", 2, 4096f, 32f),
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.False);
        Assert.That(result.Errors.Count, Is.GreaterThan(0));
    }

    [Test]
    public void StateMachine_ScatterWithoutClear_Fails()
    {
        FieldDescriptor field = Rg16("agentVelocity");
        SimPass[] passes =
        {
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void StateMachine_DisabledClear_DoesNotSatisfyScatter()
    {
        FieldDescriptor field = Rg16("agentVelocity");
        SimPass[] passes =
        {
            new StubClearPass("agentVelocity", 2, enabled: false),
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void StateMachine_NormalizeWithoutScatter_Warns()
    {
        FieldDescriptor field = Rg16("agentVelocity");
        SimPass[] passes =
        {
            new StubClearPass("agentVelocity", 2),
            new StubNormalizePass("agentVelocity", 2, 4096f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.True);
        Assert.That(result.Warnings.Count, Is.GreaterThan(0));
    }

    [Test]
    public void ChannelsMismatch_ClearVsScatter_Fails()
    {
        // Descriptor is RG16 (2). Scatter claiming 3 fails descriptor exact-match
        // (between-pass Channels agreement is defense-in-depth once all requests match descriptor).
        FieldDescriptor field = Rg16("agentVelocity");
        SimPass[] passes =
        {
            new StubClearPass("agentVelocity", 2),
            new StubScatterPass("agentVelocity", 3, 4096f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void ChannelsMismatch_WithDescriptor_Fails()
    {
        FieldDescriptor field = Rg16("agentVelocity"); // 2 channels
        Assert.That(field.ChannelCount, Is.EqualTo(2));

        SimPass[] passes =
        {
            new StubClearPass("agentVelocity", 3),
            new StubScatterPass("agentVelocity", 3, 4096f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.False);
        StringAssert.Contains("descriptor", result.Errors[0].ToLowerInvariant());
    }

    [Test]
    public void ScaleBiasMismatch_ScatterNormalize_Fails()
    {
        FieldDescriptor field = Rg16("agentVelocity");
        SimPass[] passes =
        {
            new StubClearPass("agentVelocity", 2),
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
            new StubNormalizePass("agentVelocity", 2, 2048f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.False);
        StringAssert.Contains("Scale/Bias", result.Errors[0]);
    }

    [Test]
    public void MultiRound_ClearScatterNormalizeClearScatterNormalize_Succeeds()
    {
        FieldDescriptor field = Rg16("agentVelocity");
        SimPass[] passes =
        {
            new StubClearPass("agentVelocity", 2),
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
            new StubNormalizePass("agentVelocity", 2, 4096f, 32f),
            new StubClearPass("agentVelocity", 2),
            new StubScatterPass("agentVelocity", 2, 4096f, 32f),
            new StubNormalizePass("agentVelocity", 2, 4096f, 32f),
        };

        FieldAccumPassValidator.Result result =
            FieldAccumPassValidator.Validate(passes, DescriptorMap(field));
        Assert.That(result.Success, Is.True);
    }
}
