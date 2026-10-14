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
}
