using NUnit.Framework;
using UnityEditor;
using UnityEngine;

[TestFixture]
public class SpatialHashProbePresetTests
{
    [Test]
    public void HashProbe_30k()
    {
        AssertProbe("Assets/Effects/HashProbe_30k.asset", 31);
    }

    [Test]
    public void HashProbe_100k()
    {
        AssertProbe("Assets/Effects/HashProbe_100k.asset", 46);
    }

    private static void AssertProbe(string path, int resolution)
    {
        EffectAsset asset = AssetDatabase.LoadAssetAtPath<EffectAsset>(path);
        Assert.IsNotNull(asset, path);
        CubeSource cube = (CubeSource)asset.ResolveSource();
        Assert.AreEqual(resolution, cube.Resolution);
        Assert.AreEqual(32f, cube.CubeSize);
        Assert.AreEqual(0.2f, asset.ParticleSize);
        Assert.AreEqual(5, asset.Passes.Count);
        Assert.IsInstanceOf<BuildSpatialHashPass>(asset.Passes[0]);
        Assert.IsInstanceOf<ClearVelocityPass>(asset.Passes[1]);
        Assert.IsInstanceOf<HeadingSteerPass>(asset.Passes[2]);
        Assert.IsInstanceOf<IntegratePass>(asset.Passes[3]);
        Assert.IsInstanceOf<BoxBoundsPass>(asset.Passes[4]);

        BuildSpatialHashPass hash = (BuildSpatialHashPass)asset.Passes[0];
        Assert.AreEqual(new Vector3(16f, 0f, 16f), hash.Extents);
        Assert.AreEqual(2f, hash.MinCellSize);
        Assert.IsTrue(hash.Wrap);

        BoxBoundsPass bounds = (BoxBoundsPass)asset.Passes[4];
        Assert.AreEqual(BoundsBehaviour.Wrap, bounds.Behaviour);
        Assert.AreEqual(new Vector3(16f, 0f, 16f), bounds.Extents);
    }
}
