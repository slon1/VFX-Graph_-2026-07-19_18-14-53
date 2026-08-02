using NUnit.Framework;
using UnityEngine.Experimental.Rendering;

[TestFixture]
public class FieldRequestTests
{
    [Test]
    public void ChannelsCompatible_WriteExact_Rg16_Passes()
    {
        int descriptorChannels = FieldDescriptor.GetChannelCount(GraphicsFormat.R16G16_SFloat);
        Assert.That(descriptorChannels, Is.EqualTo(2));
        Assert.That(
            FieldRequest.ChannelsCompatible(FieldAccess.WriteInPlace, 2, descriptorChannels),
            Is.True);
    }

    [Test]
    public void ChannelsCompatible_WriteExact_Rgba16_FailsWhenRequestIs2()
    {
        int descriptorChannels = FieldDescriptor.GetChannelCount(GraphicsFormat.R16G16B16A16_SFloat);
        Assert.That(descriptorChannels, Is.EqualTo(4));
        Assert.That(
            FieldRequest.ChannelsCompatible(FieldAccess.WriteInPlace, 2, descriptorChannels),
            Is.False);
        Assert.That(
            FieldRequest.ChannelsCompatible(FieldAccess.WritePingPong, 2, descriptorChannels),
            Is.False);
    }

    [Test]
    public void ChannelsCompatible_ReadAllowsWiderFormat()
    {
        int descriptorChannels = FieldDescriptor.GetChannelCount(GraphicsFormat.R16G16B16A16_SFloat);
        Assert.That(
            FieldRequest.ChannelsCompatible(FieldAccess.Read, 2, descriptorChannels),
            Is.True);
    }

    [Test]
    public void ChannelsCompatible_WritePingPong_ScalarExact_Passes()
    {
        int descriptorChannels = FieldDescriptor.GetChannelCount(GraphicsFormat.R16_SFloat);
        Assert.That(
            FieldRequest.ChannelsCompatible(FieldAccess.WritePingPong, 1, descriptorChannels),
            Is.True);
    }

    [Test]
    public void ChannelsCompatible_ClearFieldStyle_Channels1_VsRg16_Fails()
    {
        int descriptorChannels = FieldDescriptor.GetChannelCount(GraphicsFormat.R16G16_SFloat);
        Assert.That(
            FieldRequest.ChannelsCompatible(FieldAccess.WriteInPlace, 1, descriptorChannels),
            Is.False);
    }

    [Test]
    public void Equals_MatchesAllComponents()
    {
        FieldRequest a = new FieldRequest("velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        FieldRequest b = new FieldRequest("velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        FieldRequest renamed = new FieldRequest("velocity2", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        FieldRequest channelsChanged = new FieldRequest("velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 1);
        FieldRequest semanticChanged = new FieldRequest("velocity", FieldAccess.WriteInPlace, FieldSemantic.Scalar, 2);

        Assert.That(a.Equals(b), Is.True);
        Assert.That(a.Equals(renamed), Is.False);
        Assert.That(a.Equals(channelsChanged), Is.False);
        Assert.That(a.Equals(semanticChanged), Is.False);
    }

    [Test]
    public void FieldRequestSets_Single_RebuildsWhenChannelsChange()
    {
        FieldRequest[] cache = null;
        FieldRequest[] first = FieldRequestSets.Single(
            ref cache, "velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        FieldRequest[] same = FieldRequestSets.Single(
            ref cache, "velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        Assert.That(ReferenceEquals(first, same), Is.True);

        FieldRequest[] afterChannels = FieldRequestSets.Single(
            ref cache, "velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 1);
        Assert.That(ReferenceEquals(first, afterChannels), Is.False);
        Assert.That(afterChannels[0].Channels, Is.EqualTo(1));
    }

    [Test]
    public void FieldRequestSets_Single_RebuildsWhenSemanticChanges()
    {
        FieldRequest[] cache = null;
        FieldRequest[] first = FieldRequestSets.Single(
            ref cache, "velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        FieldRequest[] afterSemantic = FieldRequestSets.Single(
            ref cache, "velocity", FieldAccess.WriteInPlace, FieldSemantic.Scalar, 2);
        Assert.That(ReferenceEquals(first, afterSemantic), Is.False);
        Assert.That(afterSemantic[0].RequiredSemantic, Is.EqualTo(FieldSemantic.Scalar));
    }

    [Test]
    public void FieldRequestSets_Single_SteadyState_NoRealloc()
    {
        FieldRequest[] cache = null;
        FieldRequest[] a = FieldRequestSets.Single(
            ref cache, "velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        FieldRequest[] b = FieldRequestSets.Single(
            ref cache, "velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        FieldRequest[] c = FieldRequestSets.Single(
            ref cache, "velocity", FieldAccess.WriteInPlace, FieldSemantic.Velocity, 2);
        Assert.That(ReferenceEquals(a, b), Is.True);
        Assert.That(ReferenceEquals(b, c), Is.True);
    }
}
