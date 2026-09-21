using Godot;

namespace HeartsAlter.Scripts.UI;

/// <summary>Controls all game audio; the Master bus retains its state across scene changes.</summary>
public partial class MuteButton : TextureButton
{
	public override void _Ready()
	{
		UpdateState(AudioServer.IsBusMute(0));
		Toggled += HandleToggled;
	}

	public override void _ExitTree() => Toggled -= HandleToggled;

	private void HandleToggled(bool muted)
	{
		AudioServer.SetBusMute(0, muted);
		UpdateState(muted);
	}

	private void UpdateState(bool muted)
	{
		SetPressedNoSignal(muted);
		TooltipText = muted ? "开启声音" : "静音";
	}
}
