using Godot;

namespace HeartsAlter.Scripts;

/// <summary>Scene changes use the engine's resident ResourcePreloader, never disk I/O.</summary>
public static class SceneNavigation
{
	public const string LibraryPath = "/root/ResidentResources";

	public static Error Change(Node source, string scenePath)
	{
		if (!GodotObject.IsInstanceValid(source) || !source.IsInsideTree()) return Error.Unavailable;
		var library = source.GetNodeOrNull<ResourcePreloader>(LibraryPath);
		if (library == null || !library.HasResource(scenePath))
		{
			GD.PushError($"Scene is not registered in ResidentResources.tscn: {scenePath}");
			return Error.DoesNotExist;
		}
		if (library.GetResource(scenePath) is not PackedScene scene) return Error.InvalidData;
		return source.GetTree().ChangeSceneToPacked(scene);
	}
}
