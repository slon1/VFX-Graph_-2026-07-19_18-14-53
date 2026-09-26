using System.Reflection;
using NUnit.Framework;

[TestFixture]
public class PhysarumSteerPassTests
{
    [Test]
    public void Contract_Dynamics_PhysarumSteer_TrailRead()
    {
        PhysarumSteerPass pass = new PhysarumSteerPass();

        Assert.AreEqual(PassCategory.Dynamics, pass.Category);
        Assert.AreEqual("Physarum Steer", pass.DisplayName);
        Assert.AreEqual(22.5f, pass.SensorAngle);
        Assert.AreEqual(1f, pass.SensorDistance);
        Assert.AreEqual(45f, pass.TurnAngle);
        Assert.AreEqual(15f, pass.MoveSpeed);
        Assert.AreEqual(10f, pass.RandomWiggle);
        Assert.AreEqual(AttrSets.PositionHeading, pass.Reads);
        Assert.AreEqual(AttrSets.HeadingVelocity, pass.Writes);

        Assert.AreEqual(1, pass.FieldReads.Count);
        FieldRequest read = pass.FieldReads[0];
        Assert.AreEqual("trail", read.FieldName);
        Assert.AreEqual(FieldAccess.Read, read.Access);
        Assert.AreEqual(FieldSemantic.Scalar, read.RequiredSemantic);
        Assert.AreEqual(1, read.Channels);

        PropertyInfo kernelName = typeof(ParticleKernelPass).GetProperty(
            "KernelName", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(kernelName);
        Assert.AreEqual("PhysarumSteer", (string)kernelName.GetValue(pass));
    }
}
