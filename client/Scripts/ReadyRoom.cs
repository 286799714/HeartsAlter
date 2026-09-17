using Godot;

namespace HeartsAlter.Scripts;

/// <summary>Compatibility entry point for the retired ready-room scene.</summary>
public partial class ReadyRoom : Control
{
	public override void _Ready()
	{
		Callable.From(() => SceneNavigation.Change(this, "res://scenes/in_game/Table.tscn")).CallDeferred();
	}
}
