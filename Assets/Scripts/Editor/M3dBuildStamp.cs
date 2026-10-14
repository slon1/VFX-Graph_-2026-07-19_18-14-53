using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Writes Assets/Resources/m3d_build.txt before a player build. The harness loads it with Resources.Load.
/// Git failure does not fail the build.
/// </summary>
public sealed class M3dBuildStamp : IPreprocessBuildWithReport
{
	private const string StampPath = "Assets/Resources/m3d_build.txt";

	public int callbackOrder => 0;

	public void OnPreprocessBuild(BuildReport report)
	{
		string build = "na";
		string dirty = "na";
		string dirtyHash = "na";
		string root = Directory.GetParent(Application.dataPath).FullName;
		if (TryGit(root, "rev-parse --short HEAD", out string revision)
			&& TryGit(root, "status --porcelain -- Assets Packages ProjectSettings", out string porcelain)
			&& TryGit(root, "diff HEAD -- Assets Packages ProjectSettings", out string diff)
			&& TryGit(root, "ls-files --others --exclude-standard -- Assets Packages ProjectSettings", out string untracked))
		{
			build = revision.Trim();
			dirty = string.IsNullOrWhiteSpace(porcelain) ? "0" : "1";
			dirtyHash = HashDirty(diff, untracked);
		}
		else
		{
			UnityEngine.Debug.LogWarning("M3D build stamp: git failed, build=na dirty=na dirtyhash=na");
		}

		string directory = Path.Combine(root, "Assets", "Resources");
		Directory.CreateDirectory(directory);
		string absolute = Path.Combine(directory, "m3d_build.txt");
		File.WriteAllText(absolute, "build=" + build + "\ndirty=" + dirty + "\ndirtyhash=" + dirtyHash + "\n", new UTF8Encoding(false));
		AssetDatabase.ImportAsset(StampPath, ImportAssetOptions.ForceUpdate);
	}

	private static string HashDirty(string diff, string untracked)
	{
		string[] lines = untracked.Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
		Array.Sort(lines, StringComparer.Ordinal);
		string payload = diff.Replace("\r\n", "\n").Replace('\r', '\n') + "\n--\n" + string.Join("\n", lines);
		using (SHA1 sha = SHA1.Create())
		{
			byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
			var text = new StringBuilder(8);
			for (int i = 0; i < 4; i++)
			{
				text.Append(hash[i].ToString("x2"));
			}

			return text.ToString();
		}
	}

	private static bool TryGit(string root, string arguments, out string stdout)
	{
		stdout = "";
		try
		{
			var start = new ProcessStartInfo
			{
				FileName = "git",
				Arguments = arguments,
				WorkingDirectory = root,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			using (Process process = Process.Start(start))
			{
				if (process == null)
				{
					return false;
				}

				stdout = process.StandardOutput.ReadToEnd();
				process.StandardError.ReadToEnd();
				if (!process.WaitForExit(30000))
				{
					process.Kill();
					return false;
				}

				return process.ExitCode == 0;
			}
		}
		catch (Exception)
		{
			return false;
		}
	}
}
