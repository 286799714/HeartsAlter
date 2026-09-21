using Godot;

namespace HeartsAlter.Scripts.UI;

/// <summary>Plays a shared click sound for buttons in the containing scene.</summary>
public partial class ButtonSounds : Node
{
	[Export] public AudioStream ClickSound { get; set; }

	public override void _Ready() => BindButtons(GetParent());

	private void BindButtons(Node node)
	{
		if (node is BaseButton button)
			button.Pressed += PlayClick;
		if (node is OptionButton options)
			options.ItemSelected += _ => PlayClick();
		foreach (Node child in node.GetChildren()) BindButtons(child);
	}

	private void PlayClick()
	{
		if (ClickSound is null) return;
		var player = new AudioStreamPlayer { Stream = ClickSound };
		// Finish the click even if its action immediately removes this scene.
		GetTree().Root.AddChild(player);
		player.Finished += player.QueueFree;
		player.Play();
	}
}
