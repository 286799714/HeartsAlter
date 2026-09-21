using System;
using Godot;

namespace HeartsAlter.Scripts.UI;

/// <summary>Modal interaction shared by the quit and room-dissolved artwork.</summary>
public partial class RoomDialog : Control
{
	public event Action Confirmed;
	public event Action Canceled;
	public event Action Closed;
	public string Message { get => _message.Text; set => _message.Text = value; }
	public string ConfirmText { get => _confirmText.Text; set => _confirmText.Text = value; }

	private Control _content;
	private Label _message;
	private Label _confirmText;
	private Button _confirm;
	private Button _cancel;
	private Button _close;
	private Control _previousFocus;

	public override void _Ready()
	{
		_content = GetNode<Control>("%DialogContent");
		_message = GetNode<Label>("%Message");
		_confirmText = GetNode<Label>("%ConfirmText");
		_confirm = GetNode<Button>("%ConfirmButton");
		_cancel = GetNodeOrNull<Button>("%CancelButton");
		_close = GetNode<Button>("%CloseButton");
		_confirm.Pressed += () => { Close(); Confirmed?.Invoke(); };
		if (_cancel is not null) _cancel.Pressed += Cancel;
		_close.Pressed += Cancel;
		Resized += FitDialog;
		FitDialog();
	}

	public void Open()
	{
		_previousFocus = GetViewport().GuiGetFocusOwner();
		Show();
		FitDialog();
		(_cancel ?? _confirm).GrabFocus();
	}

	public void Close()
	{
		if (!Visible) return;
		Hide();
		if (IsInstanceValid(_previousFocus) && _previousFocus.IsVisibleInTree()) _previousFocus.GrabFocus();
		Closed?.Invoke();
	}

	private void Cancel()
	{
		Close();
		Canceled?.Invoke();
	}

	public override void _Input(InputEvent input)
	{
		if (!IsVisibleInTree() || input is not InputEventKey { Pressed: true } key) return;
		if (key.Keycode == Key.Escape)
		{
			Cancel();
			GetViewport().SetInputAsHandled();
		}
		else if (key.Keycode is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down)
		{
			Button[] controls = _cancel is null ? new[] { _confirm, _close } : new[] { _cancel, _confirm, _close };
			int current = Array.IndexOf(controls, GetViewport().GuiGetFocusOwner());
			int direction = key.Keycode is Key.Left or Key.Up || key.Keycode == Key.Tab && key.ShiftPressed ? -1 : 1;
			controls[(current + direction + controls.Length) % controls.Length].GrabFocus();
			GetViewport().SetInputAsHandled();
		}
	}

	private void FitDialog()
	{
		var designSize = new Vector2(1200, 730);
		float scale = Mathf.Min(0.667f, Mathf.Min((Size.X - 32) / designSize.X, (Size.Y - 32) / designSize.Y));
		_content.Scale = Vector2.One * Mathf.Max(0.1f, scale);
		_content.Position = (Size - designSize * _content.Scale) / 2;
	}
}
