using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Godot;
using HeartsAlter.Scripts;

namespace HeartsAlter.Tests;

/// <summary>Exercises installation identity persistence using disposable files.</summary>
public partial class DeviceIdentitySmoke : Node
{
	public override void _Ready()
	{
		string directory = Path.Combine(Path.GetTempPath(), "hearts-identity-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		int exitCode = 0;
		try
		{
			string path = Path.Combine(directory, "device_identity.cfg");
			string first = DeviceIdentity.LoadOrCreateInstallationId(path);
			Check(first.Length == 64, "Expected the existing SHA-256 wire format");
			string originalFile = File.ReadAllText(path);
			Check(DeviceIdentity.LoadOrCreateInstallationId(path) == first, "A fresh read changed identity");
			Check(File.ReadAllText(path) == originalFile, "Reading rewrote the identity file");
			string other = DeviceIdentity.LoadOrCreateInstallationId(Path.Combine(directory, "other.cfg"));
			Check(other != first, "Separate installations share the same identity");

			const string legacyId = "ABCDEF1234567890ABCDEF1234567890";
			File.WriteAllText(path, $"[identity]\nid=\"{legacyId}\"\n");
			string legacyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("HeartsAlter:install:" + legacyId))).ToLowerInvariant();
			Check(DeviceIdentity.LoadOrCreateInstallationId(path) == legacyKey, "Existing fallback UUID changed its wire key");

			foreach (string damaged in new[] { "", "[identity]\n", "[identity]\nid=123\n",
				"[identity]\nid=\"invalid\"\n", "[identity]\nid=\"00000000000000000000000000000000\"\n" })
			{
				File.WriteAllText(path, damaged);
				ExpectFailure(() => DeviceIdentity.LoadOrCreateInstallationId(path));
				Check(File.ReadAllText(path) == damaged, "Damaged identity was silently replaced");
			}

			File.WriteAllText(path, originalFile);
			using (var locked = new FileStream(path, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None))
				ExpectFailure(() => DeviceIdentity.LoadOrCreateInstallationId(path));
			Check(File.ReadAllText(path) == originalFile, "Read failure overwrote the identity");
			Check(DeviceIdentity.LoadOrCreateInstallationId(path) == first, "Retry after a read failure changed identity");

			string missingParent = Path.Combine(directory, "missing", "device_identity.cfg");
			ExpectFailure(() => DeviceIdentity.LoadOrCreateInstallationId(missingParent));
			Check(!File.Exists(missingParent), "Failed creation left a usable identity");
			Directory.CreateDirectory(Path.GetDirectoryName(missingParent));
			string retry = DeviceIdentity.LoadOrCreateInstallationId(missingParent);
			Check(DeviceIdentity.LoadOrCreateInstallationId(missingParent) == retry, "Retry after a write failure did not persist");
			GD.Print("DEVICE_IDENTITY_SMOKE_OK: create, fresh read, legacy UUID, corruption, locked file and write failure");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError(exception.ToString());
		}
		finally
		{
			// Only this run's explicitly created temporary files/directories are removed.
			foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
			string child = Path.Combine(directory, "missing");
			if (Directory.Exists(child))
			{
				foreach (string file in Directory.GetFiles(child)) File.Delete(file);
				Directory.Delete(child);
			}
			Directory.Delete(directory);
			GetTree().Quit(exitCode);
		}
	}

	private static void ExpectFailure(Func<string> action)
	{
		try { action(); }
		catch (InvalidOperationException) { return; }
		throw new InvalidOperationException("An unreadable or unwritable identity was accepted");
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
