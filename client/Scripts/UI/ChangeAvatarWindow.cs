using System;
using Godot;

namespace HeartsAlter.Scripts.UI;

/// <summary>Selection and modal input for the imported avatar artwork.</summary>
public partial class ChangeAvatarWindow : Node
{
	public event Action<int> ConfirmRequested;
	public event Action Closed;
	public bool IsOpen => _view.Visible;
	public int SelectedAvatarId { get; private set; }

	private Control _view;
	private Control _content;
	private Control _confirmArt;
	private Button _confirm;
	private Button _close;
	private Label _status;
	private Label _confirmText;
	private readonly Button[] _choices = new Button[4];
	private bool _saving;

	public override void _Ready()
	{
		_view = GetParent<Control>();
		_content = GetNode<Control>("%AvatarDialogContent");
		_confirmArt = GetNode<Control>("%ConfirmAvatarArt");
		_confirm = GetNode<Button>("%ConfirmAvatar");
		_close = GetNode<Button>("%CloseAvatar");
		_status = GetNode<Label>("%AvatarStatus");
		_confirmText = GetNode<Label>("%ConfirmAvatarText");
		for (int index = 0; index < _choices.Length; index++)
		{
			int avatarId = index + 1;
			_choices[index] = GetNode<Button>($"%AvatarChoice{avatarId}");
			_choices[index].Pressed += () => SelectAvatar(avatarId);
		}
		_confirm.Pressed += () => { if (!_saving) ConfirmRequested?.Invoke(SelectedAvatarId); };
		_close.Pressed += Close;
		_view.Resized += FitDialog;
		FitDialog();
	}

	public void Open(int avatarId)
	{
		SetSaving(false);
		_view.Show();
		FitDialog();
		SelectAvatar(Math.Clamp(avatarId, 1, _choices.Length));
		_choices[SelectedAvatarId - 1].GrabFocus();
	}

	public void Close()
	{
		if (_saving || !IsOpen) return;
		_view.Hide();
		Closed?.Invoke();
	}

	public void SetSaving(bool saving)
	{
		_saving = saving;
		_confirm.Disabled = saving;
		_close.Disabled = saving;
		foreach (Button choice in _choices) choice.Disabled = saving;
		_confirmArt.Modulate = saving ? new Color(1, 1, 1, 0.55f) : Colors.White;
		_confirmText.Text = saving ? "保存中…" : "替换头像";
	}

	public void ShowError(string message)
	{
		_status.Text = message;
		_status.Modulate = new Color(0.65f, 0.12f, 0.1f);
	}

	public override void _Input(InputEvent input)
	{
		if (!IsOpen || input is not InputEventKey { Pressed: true } key) return;
		if (key.Keycode == Key.Escape)
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
		else if (key.Keycode is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down)
		{
			// Keep keyboard navigation within the modal, including while saving.
			Button[] controls = { _choices[0], _choices[1], _choices[2], _choices[3], _confirm, _close };
			int current = Array.IndexOf(controls, GetViewport().GuiGetFocusOwner());
			int direction = key.Keycode is Key.Left or Key.Up || key.Keycode == Key.Tab && key.ShiftPressed ? -1 : 1;
			for (int step = 1; step <= controls.Length; step++)
			{
				Button next = controls[(current + direction * step + controls.Length * 2) % controls.Length];
				if (next.Disabled) continue;
				next.GrabFocus();
				break;
			}
			GetViewport().SetInputAsHandled();
		}
	}

	private void SelectAvatar(int avatarId)
	{
		if (_saving) return;
		SelectedAvatarId = avatarId;
		for (int index = 0; index < _choices.Length; index++)
			_choices[index].SetPressedNoSignal(index + 1 == avatarId);
		_status.Text = "选择喜欢的头像，点击下方按钮保存";
		_status.Modulate = Colors.White;
	}

	private void FitDialog()
	{
		var designSize = new Vector2(1200, 730);
		float scale = Mathf.Min(0.667f, Mathf.Min((_view.Size.X - 32) / designSize.X, (_view.Size.Y - 32) / designSize.Y));
		_content.Scale = Vector2.One * Mathf.Max(0.1f, scale);
		_content.Position = (_view.Size - designSize * _content.Scale) / 2;
	}
}
