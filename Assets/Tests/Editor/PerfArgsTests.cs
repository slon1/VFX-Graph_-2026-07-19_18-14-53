using NUnit.Framework;

public class PerfArgsTests
{
	[Test]
	public void Parse_NoArguments_KeepsHarnessDefaults()
	{
		PerfArgs parsed = PerfArgs.Parse(new string[0]);

		Assert.That(parsed.HasForce, Is.False);
		Assert.That(parsed.HasRender, Is.False);
		Assert.That(parsed.HasMaxNeighbors, Is.False);
		Assert.That(parsed.HasWarmup, Is.False);
		Assert.That(parsed.HasMeasure, Is.False);
		Assert.That(parsed.WarmupSeconds, Is.EqualTo(30f));
		Assert.That(parsed.MeasureSeconds, Is.EqualTo(30f));
		Assert.That(parsed.Autorun, Is.False);
		Assert.That(parsed.Quit, Is.False);
		Assert.That(parsed.Tag, Is.EqualTo(""));
		Assert.That(parsed.HasOverride, Is.False);
		Assert.That(parsed.Warnings, Is.Empty);
	}

	[Test]
	public void Parse_NullArguments_DoesNotThrow()
	{
		PerfArgs parsed = PerfArgs.Parse(null);

		Assert.That(parsed.HasOverride, Is.False);
		Assert.That(parsed.Warnings, Is.Empty);
	}

	[Test]
	public void Parse_AllKeys_ReadsTheGateCommand()
	{
		PerfArgs parsed = PerfArgs.Parse(new[]
		{
			"-m3d_autorun",
			"-m3d_force", "1",
			"-m3d_render", "0",
			"-m3d_maxn", "48",
			"-m3d_warmup", "15",
			"-m3d_measure", "10",
			"-m3d_quit",
			"-m3d_tag", "test",
		});

		Assert.That(parsed.Autorun, Is.True);
		Assert.That(parsed.HasForce, Is.True);
		Assert.That(parsed.Force, Is.True);
		Assert.That(parsed.HasRender, Is.True);
		Assert.That(parsed.Render, Is.False);
		Assert.That(parsed.HasMaxNeighbors, Is.True);
		Assert.That(parsed.MaxNeighbors, Is.EqualTo(48));
		Assert.That(parsed.HasWarmup, Is.True);
		Assert.That(parsed.WarmupSeconds, Is.EqualTo(15f));
		Assert.That(parsed.HasMeasure, Is.True);
		Assert.That(parsed.MeasureSeconds, Is.EqualTo(10f));
		Assert.That(parsed.Quit, Is.True);
		Assert.That(parsed.Tag, Is.EqualTo("test"));
		Assert.That(parsed.Warnings, Is.Empty);
	}

	[Test]
	public void Parse_UnknownKeys_AreIgnored()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-batchmode", "hello", "-m3d_force", "0" });

		Assert.That(parsed.HasForce, Is.True);
		Assert.That(parsed.Force, Is.False);
		Assert.That(parsed.Warnings, Is.Empty);
	}

	[Test]
	public void Parse_MissingValue_DoesNotThrowAndWarns()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-m3d_force", "-m3d_quit", "-m3d_tag" });

		Assert.That(parsed.HasForce, Is.False);
		Assert.That(parsed.Quit, Is.True);
		Assert.That(parsed.Tag, Is.EqualTo(""));
		Assert.That(parsed.Warnings, Is.EqualTo(new[] { "force=", "tag=" }));
	}

	[Test]
	public void Parse_BadWarmupAndMeasure_FallBackToThirty()
	{
		PerfArgs parsed = PerfArgs.Parse(new[]
		{
			"-m3d_warmup", "0",
			"-m3d_measure", "-4",
			"-m3d_warmup", "nope",
		});

		Assert.That(parsed.HasWarmup, Is.True);
		Assert.That(parsed.WarmupSeconds, Is.EqualTo(30f));
		Assert.That(parsed.HasMeasure, Is.True);
		Assert.That(parsed.MeasureSeconds, Is.EqualTo(30f));
		Assert.That(parsed.Warnings, Is.EqualTo(new[] { "warmup=0", "measure=-4", "warmup=nope" }));
	}

	[Test]
	public void Parse_ForceAndRenderOutsideZeroOne_DoNotOverride()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-m3d_force", "2", "-m3d_render", "-1", "-m3d_force", "1" });

		Assert.That(parsed.HasForce, Is.True);
		Assert.That(parsed.Force, Is.True);
		Assert.That(parsed.HasRender, Is.False);
		Assert.That(parsed.Warnings, Is.EqualTo(new[] { "force=2", "render=-1" }));
	}

	[Test]
	public void Parse_MaxNeighbors_AcceptsZeroAndRejectsNegative()
	{
		PerfArgs zero = PerfArgs.Parse(new[] { "-m3d_maxn", "0" });
		PerfArgs bad = PerfArgs.Parse(new[] { "-m3d_maxn", "-3", "-m3d_maxn", "12.5" });

		Assert.That(zero.HasMaxNeighbors, Is.True);
		Assert.That(zero.MaxNeighbors, Is.EqualTo(0));
		Assert.That(zero.Warnings, Is.Empty);
		Assert.That(bad.HasMaxNeighbors, Is.False);
		Assert.That(bad.Warnings, Is.EqualTo(new[] { "maxn=-3", "maxn=12.5" }));
	}

	[Test]
	public void Parse_FlagsDoNotConsumeTheNextSwitch()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-m3d_autorun", "-m3d_render", "1" });

		Assert.That(parsed.Autorun, Is.True);
		Assert.That(parsed.HasRender, Is.True);
		Assert.That(parsed.Render, Is.True);
	}

	[Test]
	public void Parse_Tag_ReplacesCharactersOutsideTheAllowList()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-m3d_tag", "a b,c/д" });
		PerfArgs clean = PerfArgs.Parse(new[] { "-m3d_tag", "build.1-a_2" });

		Assert.That(parsed.Tag, Is.EqualTo("a_b_c__"));
		Assert.That(parsed.Warnings, Is.EqualTo(new[] { "tag=a b,c/д" }));
		Assert.That(clean.Tag, Is.EqualTo("build.1-a_2"));
		Assert.That(clean.Warnings, Is.Empty);
	}

	[Test]
	public void FormatRun_UsesNaWhenTheWorldDidNotSupplyAValue()
	{
		string args = PerfArgs.FormatRun(null, false, null, 15f, 10.5f, true, true, "test");

		Assert.That(args, Is.EqualTo("force=na,render=0,maxn=na,warmup=15,measure=10.5,autorun=1,quit=1,tag=test"));
	}

	[Test]
	public void Parse_Scene_ReadsASceneName()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-m3d_scene", "Boids_Hash_test200k" });

		Assert.That(parsed.HasScene, Is.True);
		Assert.That(parsed.Scene, Is.EqualTo("Boids_Hash_test200k"));
		Assert.That(parsed.Warnings, Is.Empty);
		Assert.That(parsed.HasOverride, Is.False);
	}

	[Test]
	public void Parse_Scene_RejectsEmptyAndIllegalNames()
	{
		PerfArgs missing = PerfArgs.Parse(new[] { "-m3d_scene" });
		PerfArgs illegal = PerfArgs.Parse(new[] { "-m3d_scene", "Boids Hash" });

		Assert.That(missing.HasScene, Is.False);
		Assert.That(illegal.HasScene, Is.False);
		Assert.That(missing.Warnings, Is.EqualTo(new[] { "scene=" }));
		Assert.That(illegal.Warnings, Is.EqualTo(new[] { "scene=Boids Hash" }));
	}

	[Test]
	public void FormatRun_AppendsSceneWhenItWasSupplied()
	{
		string args = PerfArgs.FormatRun(true, true, 48, 60f, 25f, true, true, "A1", "Boids_Hash_test");

		Assert.That(args, Is.EqualTo(
			"force=1,render=1,maxn=48,warmup=60,measure=25,autorun=1,quit=1,tag=A1,scene=Boids_Hash_test"));
	}

	[Test]
	public void Parse_Walk_Absent_DoesNotOverride()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-m3d_tag", "A1" });

		Assert.That(parsed.HasWalk, Is.False);
		Assert.That(parsed.Walk, Is.EqualTo(0));
		Assert.That(parsed.HasOverride, Is.False);
		Assert.That(parsed.Warnings, Is.Empty);
		Assert.That(
			PerfArgs.FormatRun(null, null, null, 30f, 30f, false, false, ""),
			Is.EqualTo("force=na,render=na,maxn=na,warmup=30,measure=30,autorun=0,quit=0,tag="));
	}

	[Test]
	public void Parse_Walk_AcceptsZeroOneAndTwo()
	{
		PerfArgs zero = PerfArgs.Parse(new[] { "-m3d_walk", "0" });
		PerfArgs one = PerfArgs.Parse(new[] { "-m3d_walk", "1" });
		PerfArgs two = PerfArgs.Parse(new[] { "-m3d_walk", "2" });

		Assert.That(zero.HasWalk, Is.True);
		Assert.That(zero.Walk, Is.EqualTo(0));
		Assert.That(one.Walk, Is.EqualTo(1));
		Assert.That(two.Walk, Is.EqualTo(2));
		Assert.That(one.HasOverride, Is.True);
		Assert.That(one.Warnings, Is.Empty);
	}

	[Test]
	public void Parse_Walk_RejectsOutOfRangeMissingAndText()
	{
		PerfArgs three = PerfArgs.Parse(new[] { "-m3d_walk", "3" });
		PerfArgs negative = PerfArgs.Parse(new[] { "-m3d_walk", "-1" });
		PerfArgs text = PerfArgs.Parse(new[] { "-m3d_walk", "abc" });
		PerfArgs missing = PerfArgs.Parse(new[] { "-m3d_walk" });

		Assert.That(three.HasWalk, Is.False);
		Assert.That(negative.HasWalk, Is.False);
		Assert.That(text.HasWalk, Is.False);
		Assert.That(missing.HasWalk, Is.False);
		Assert.That(three.Warnings, Is.EqualTo(new[] { "walk=3" }));
		Assert.That(negative.Warnings, Is.EqualTo(new[] { "walk=-1" }));
		Assert.That(text.Warnings, Is.EqualTo(new[] { "walk=abc" }));
		Assert.That(missing.Warnings, Is.EqualTo(new[] { "walk=" }));
	}

	[Test]
	public void Parse_Walk_CombinesWithMaxNeighbors()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-m3d_walk", "1", "-m3d_maxn", "48" });

		Assert.That(parsed.HasWalk, Is.True);
		Assert.That(parsed.Walk, Is.EqualTo(1));
		Assert.That(parsed.HasMaxNeighbors, Is.True);
		Assert.That(parsed.MaxNeighbors, Is.EqualTo(48));
		Assert.That(parsed.HasOverride, Is.True);
		Assert.That(parsed.Warnings, Is.Empty);
		Assert.That(
			PerfArgs.FormatRun(true, false, 48, 60f, 25f, true, true, "B1", null, 1),
			Is.EqualTo("force=1,render=0,maxn=48,warmup=60,measure=25,autorun=1,quit=1,tag=B1,walk=1"));
	}

	[Test]
	public void Parse_Cam_AcceptsPositiveSize()
	{
		PerfArgs parsed = PerfArgs.Parse(new[] { "-m3d_cam", "46.5" });

		Assert.That(parsed.HasCam, Is.True);
		Assert.That(parsed.Cam, Is.EqualTo(46.5f).Within(1e-4f));
		Assert.That(parsed.HasOverride, Is.False);
		Assert.That(parsed.Warnings, Is.Empty);
		Assert.That(
			PerfArgs.FormatRun(true, true, 48, 60f, 25f, true, true, "A1", "Boids_Hash_test", 0),
			Does.Not.Contain("cam="));
	}

	[Test]
	public void Parse_Cam_RejectsNonPositive()
	{
		PerfArgs zero = PerfArgs.Parse(new[] { "-m3d_cam", "0" });
		PerfArgs negative = PerfArgs.Parse(new[] { "-m3d_cam", "-2" });
		PerfArgs text = PerfArgs.Parse(new[] { "-m3d_cam", "abc" });
		PerfArgs missing = PerfArgs.Parse(new[] { "-m3d_cam" });

		Assert.That(zero.HasCam, Is.False);
		Assert.That(negative.HasCam, Is.False);
		Assert.That(text.HasCam, Is.False);
		Assert.That(missing.HasCam, Is.False);
		Assert.That(zero.Warnings, Is.EqualTo(new[] { "cam=0" }));
		Assert.That(negative.Warnings, Is.EqualTo(new[] { "cam=-2" }));
		Assert.That(text.Warnings, Is.EqualTo(new[] { "cam=abc" }));
		Assert.That(missing.Warnings, Is.EqualTo(new[] { "cam=" }));
	}

	[Test]
	public void Parse_Cap_AcceptsZeroAndOne()
	{
		PerfArgs on = PerfArgs.Parse(new[] { "-m3d_cap", "1" });
		PerfArgs off = PerfArgs.Parse(new[] { "-m3d_cap", "0" });

		Assert.That(on.HasCap, Is.True);
		Assert.That(on.Cap, Is.True);
		Assert.That(off.HasCap, Is.True);
		Assert.That(off.Cap, Is.False);
		Assert.That(on.HasOverride, Is.False);
		Assert.That(on.Warnings, Is.Empty);
		Assert.That(
			PerfArgs.FormatRun(true, true, 48, 60f, 25f, true, true, "B1"),
			Does.Not.Contain("cap="));
	}

	[Test]
	public void Parse_Cap_RejectsOtherValues()
	{
		PerfArgs two = PerfArgs.Parse(new[] { "-m3d_cap", "2" });
		PerfArgs text = PerfArgs.Parse(new[] { "-m3d_cap", "abc" });
		PerfArgs missing = PerfArgs.Parse(new[] { "-m3d_cap" });

		Assert.That(two.HasCap, Is.False);
		Assert.That(text.HasCap, Is.False);
		Assert.That(missing.HasCap, Is.False);
		Assert.That(two.Warnings, Is.EqualTo(new[] { "cap=2" }));
		Assert.That(text.Warnings, Is.EqualTo(new[] { "cap=abc" }));
		Assert.That(missing.Warnings, Is.EqualTo(new[] { "cap=" }));
	}

	[Test]
	public void Parse_HashRep_AcceptsOneThroughSixteen()
	{
		PerfArgs once = PerfArgs.Parse(new[] { "-m3d_hashrep", "1" });
		PerfArgs sixteen = PerfArgs.Parse(new[] { "-m3d_hashrep", "16" });

		Assert.That(once.HasHashRep, Is.True);
		Assert.That(once.HashRep, Is.EqualTo(1));
		Assert.That(sixteen.HashRep, Is.EqualTo(16));
		Assert.That(once.HasOverride, Is.False);
		Assert.That(once.Warnings, Is.Empty);
		Assert.That(
			PerfArgs.FormatRun(true, false, 48, 60f, 25f, true, true, "B1", null, 1),
			Does.Not.Contain("hashrep="));
	}

	[Test]
	public void Parse_HashRep_RejectsOutOfRange()
	{
		PerfArgs zero = PerfArgs.Parse(new[] { "-m3d_hashrep", "0" });
		PerfArgs big = PerfArgs.Parse(new[] { "-m3d_hashrep", "17" });
		PerfArgs text = PerfArgs.Parse(new[] { "-m3d_hashrep", "abc" });
		PerfArgs missing = PerfArgs.Parse(new[] { "-m3d_hashrep" });

		Assert.That(zero.HasHashRep, Is.False);
		Assert.That(big.HasHashRep, Is.False);
		Assert.That(text.HasHashRep, Is.False);
		Assert.That(missing.HasHashRep, Is.False);
		Assert.That(zero.Warnings, Is.EqualTo(new[] { "hashrep=0" }));
		Assert.That(big.Warnings, Is.EqualTo(new[] { "hashrep=17" }));
		Assert.That(text.Warnings, Is.EqualTo(new[] { "hashrep=abc" }));
		Assert.That(missing.Warnings, Is.EqualTo(new[] { "hashrep=" }));
	}
}
