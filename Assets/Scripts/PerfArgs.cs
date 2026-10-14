using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Command-line switches for <see cref="FPSDisplay"/>. No Unity API: EditMode tests call <see cref="Parse"/>.
/// Absent switches leave the harness on its serialized defaults. <see cref="Autorun"/> is accepted and ignored.
/// </summary>
public sealed class PerfArgs
{
	public const float DefaultSeconds = 30f;

	private readonly List<string> warnings = new List<string>();

	public bool HasForce { get; private set; }
	public bool Force { get; private set; }
	public bool HasRender { get; private set; }
	public bool Render { get; private set; }
	public bool HasMaxNeighbors { get; private set; }
	public int MaxNeighbors { get; private set; }
	public bool HasWarmup { get; private set; }
	public float WarmupSeconds { get; private set; } = DefaultSeconds;
	public bool HasMeasure { get; private set; }
	public float MeasureSeconds { get; private set; } = DefaultSeconds;
	public bool Autorun { get; private set; }
	public bool Quit { get; private set; }
	public string Tag { get; private set; } = "";

	public bool HasOverride => HasForce || HasRender || HasMaxNeighbors;

	public IReadOnlyList<string> Warnings => warnings;

	public static PerfArgs Parse(string[] args)
	{
		var parsed = new PerfArgs();
		if (args == null)
		{
			return parsed;
		}

		for (int i = 0; i < args.Length; i++)
		{
			string token = args[i];
			if (token == "-m3d_autorun")
			{
				parsed.Autorun = true;
				continue;
			}

			if (token == "-m3d_quit")
			{
				parsed.Quit = true;
				continue;
			}

			if (token == "-m3d_force")
			{
				parsed.ReadForce(NextValue(args, ref i));
				continue;
			}

			if (token == "-m3d_render")
			{
				parsed.ReadRender(NextValue(args, ref i));
				continue;
			}

			if (token == "-m3d_maxn")
			{
				parsed.ReadMaxNeighbors(NextValue(args, ref i));
				continue;
			}

			if (token == "-m3d_warmup")
			{
				parsed.ReadSeconds(NextValue(args, ref i), warmup: true);
				continue;
			}

			if (token == "-m3d_measure")
			{
				parsed.ReadSeconds(NextValue(args, ref i), warmup: false);
				continue;
			}

			if (token == "-m3d_tag")
			{
				parsed.ReadTag(NextValue(args, ref i));
			}
		}

		return parsed;
	}

	/// <summary>
	/// One token for <c>M3D_PERF_INFO</c>. Null force, render, or max-neighbors becomes <c>na</c>.
	/// </summary>
	public static string FormatRun(
		bool? force,
		bool? render,
		int? maxNeighbors,
		float warmupSeconds,
		float measureSeconds,
		bool autorun,
		bool quit,
		string tag)
	{
		return "force=" + Bit(force)
			+ ",render=" + Bit(render)
			+ ",maxn=" + (maxNeighbors.HasValue ? maxNeighbors.Value.ToString(CultureInfo.InvariantCulture) : "na")
			+ ",warmup=" + FormatSeconds(warmupSeconds)
			+ ",measure=" + FormatSeconds(measureSeconds)
			+ ",autorun=" + (autorun ? "1" : "0")
			+ ",quit=" + (quit ? "1" : "0")
			+ ",tag=" + (tag ?? "");
	}

	private static string Bit(bool? value)
	{
		if (!value.HasValue)
		{
			return "na";
		}

		return value.Value ? "1" : "0";
	}

	public static string FormatSeconds(float seconds)
	{
		return seconds.ToString("0.###", CultureInfo.InvariantCulture);
	}

	private static string NextValue(string[] args, ref int index)
	{
		if (index + 1 >= args.Length)
		{
			return null;
		}

		string next = args[index + 1];
		if (!string.IsNullOrEmpty(next) && next[0] == '-' && !IsNumeric(next))
		{
			return null;
		}

		index++;
		return next;
	}

	private static bool IsNumeric(string token)
	{
		return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
	}

	private void ReadForce(string value)
	{
		if (value == "0" || value == "1")
		{
			HasForce = true;
			Force = value == "1";
			return;
		}

		warnings.Add(value == null ? "force=" : "force=" + value);
	}

	private void ReadRender(string value)
	{
		if (value == "0" || value == "1")
		{
			HasRender = true;
			Render = value == "1";
			return;
		}

		warnings.Add(value == null ? "render=" : "render=" + value);
	}

	private void ReadMaxNeighbors(string value)
	{
		if (value != null
			&& int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
			&& count >= 0)
		{
			HasMaxNeighbors = true;
			MaxNeighbors = count;
			return;
		}

		warnings.Add(value == null ? "maxn=" : "maxn=" + value);
	}

	private void ReadSeconds(string value, bool warmup)
	{
		string key = warmup ? "warmup" : "measure";
		if (value != null
			&& float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)
			&& seconds > 0f)
		{
			SetSeconds(warmup, seconds);
			return;
		}

		warnings.Add(value == null ? key + "=" : key + "=" + value);
		if (value != null)
		{
			SetSeconds(warmup, DefaultSeconds);
		}
	}

	private void SetSeconds(bool warmup, float seconds)
	{
		if (warmup)
		{
			HasWarmup = true;
			WarmupSeconds = seconds;
			return;
		}

		HasMeasure = true;
		MeasureSeconds = seconds;
	}

	private void ReadTag(string value)
	{
		if (value == null)
		{
			warnings.Add("tag=");
			return;
		}

		Tag = value;
	}
}
