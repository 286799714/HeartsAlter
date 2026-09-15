using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Godot;

namespace HeartsAlter.Scripts;

/// <summary>Persistent installation identity for the first version's account-free saves.</summary>
public static class DeviceIdentity
{
	private const string IdentityPath = "user://device_identity.cfg";
	private static string _deviceId;

	public static string GetDeviceId()
	{
		if (!string.IsNullOrEmpty(_deviceId)) return _deviceId;
		// Launch multiple local test players with distinct --device-id=<value>
		// arguments after Godot's -- separator. Normal launches use the saved UUID.
		if (OS.IsDebugBuild())
		{
			foreach (string argument in OS.GetCmdlineUserArgs())
				if (argument.StartsWith("--device-id=", StringComparison.Ordinal) && argument.Length > 12)
					return _deviceId = Hash("debug:" + argument[12..]);
		}
		// Cache only after a successful read/write. A failed attempt can be retried
		// without accidentally using a new, unsaved identity for the connection.
		return _deviceId = LoadOrCreateInstallationId(IdentityPath);
	}

	internal static string LoadOrCreateInstallationId(string identityPath)
	{
		using var config = new ConfigFile();
		Error loadError = config.Load(identityPath);
		if (loadError == Error.Ok)
			return ReadInstallationId(config);
		if (loadError != Error.FileNotFound)
			throw new InvalidOperationException($"无法读取本地安装标识，请检查文件后重试：{loadError}");

		string generated = Guid.NewGuid().ToString("N");
		config.SetValue("identity", "id", generated);
		try
		{
			// Never truncate a file another process may have created since Load.
			// Concurrent creation/read failures stop this connection and can be retried.
			using var file = new FileStream(ProjectSettings.GlobalizePath(identityPath),
				FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None);
			file.Write(Encoding.UTF8.GetBytes(config.EncodeToText()));
			file.Flush(flushToDisk: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new InvalidOperationException("无法保存本地安装标识，请检查文件权限后重试", exception);
		}

		// Verify the persisted representation before allowing a server connection.
		using var persisted = new ConfigFile();
		Error verifyError = persisted.Load(identityPath);
		if (verifyError != Error.Ok)
			throw new InvalidOperationException($"无法读取刚保存的安装标识，请重试：{verifyError}");
		return ReadInstallationId(persisted);
	}

	private static string ReadInstallationId(ConfigFile config)
	{
		Variant value = config.GetValue("identity", "id", "");
		if (value.VariantType != Variant.Type.String)
			throw new InvalidOperationException("本地安装标识损坏，请恢复标识文件后重试");
		string saved = value.AsString();
		if (!Guid.TryParseExact(saved, "N", out Guid id) || id == Guid.Empty)
			throw new InvalidOperationException("本地安装标识损坏，请恢复标识文件后重试");
		// Keep the existing fallback UUID's wire key, including its exact casing.
		return Hash("install:" + saved);
	}

	private static string Hash(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("HeartsAlter:" + value))).ToLowerInvariant();
}
