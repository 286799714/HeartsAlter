using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Scripts.Tutorial;

/// <summary>Scene-authored overlay. Only target tracking and input interception happen in code.</summary>
public partial class TutorialSpotlight : Control
{
	public event Action AdvanceRequested;
	private ColorRect _shade;
	private ShaderMaterial _material;
	private Control _panel, _topSlot, _bottomSlot;
	private Label _text, _progress, _continue;
	private Control[] _targets = Array.Empty<Control>();
	private readonly Dictionary<CardControl, int> _raisedCards = new();
	public IReadOnlyList<Control> Targets => _targets;
	public IReadOnlyList<Rect2> FocusRects { get; private set; } = Array.Empty<Rect2>();
	public string CurrentText => _text.Text;

	public override void _Ready()
	{
		_shade = GetNode<ColorRect>("Shade");
		_material = (ShaderMaterial)_shade.Material;
		_panel = GetNode<Control>("TopSlot/InstructionPanel");
		_topSlot = GetNode<Control>("TopSlot");
		_bottomSlot = GetNode<Control>("BottomSlot");
		_text = _panel.GetNode<Label>("Column/Instruction");
		_progress = _panel.GetNode<Label>("Column/GuideProgress");
		_continue = _panel.GetNode<Label>("Column/GuideContinue");
		HideGuide();
	}

	public void ShowPage(string text, int index, int count, IEnumerable<Control> targets, bool above)
	{
		RestoreCardOrder();
		_targets = targets.Where(target => target is not null && IsInstanceValid(target)).Take(16).ToArray();
		if (_targets.Length == 0) throw new InvalidOperationException("教学聚焦目标不存在。");
		// Show the face of a focused hand card above its neighbours, without
		// moving or selecting it. Restore the original stacking after the page.
		foreach (CardControl card in _targets.OfType<CardControl>())
		{
			if (card.GetParent() is not MainHandLayout) continue;
			_raisedCards[card] = card.ZIndex;
			card.ZIndex += 100;
		}
		Control slot = above ? _topSlot : _bottomSlot;
		if (_panel.GetParent() != slot) _panel.Reparent(slot, keepGlobalTransform: false);
		_text.Text = text;
		_progress.Text = $"讲解 {index + 1} / {count}";
		_continue.Text = "点击屏幕继续";
		Show();
		_panel.Show();
		SetProcess(true);
		SetProcessInput(true);
		UpdateFocus();
	}

	public void HideGuide()
	{
		RestoreCardOrder();
		Hide();
		_panel.Hide();
		_targets = Array.Empty<Control>();
		FocusRects = Array.Empty<Rect2>();
		_material.SetShaderParameter("focus_count", 0);
		SetProcess(false);
		SetProcessInput(false);
	}

	public override void _ExitTree() => RestoreCardOrder();

	private void RestoreCardOrder()
	{
		foreach (var (card, index) in _raisedCards)
			if (IsInstanceValid(card)) card.ZIndex = index;
		_raisedCards.Clear();
	}

	public override void _Process(double delta) => UpdateFocus();

	private void UpdateFocus()
	{
		Transform2D toOverlay = _shade.GetGlobalTransformWithCanvas().AffineInverse();
		var rectangles = new List<Rect2>();
		foreach (Control target in _targets)
		{
			if (!IsInstanceValid(target) || !target.IsVisibleInTree()) continue;
			Transform2D transform = toOverlay * target.GetGlobalTransformWithCanvas();
			Vector2[] corners = { transform * Vector2.Zero, transform * new Vector2(target.Size.X, 0),
				transform * target.Size, transform * new Vector2(0, target.Size.Y) };
			var rect = new Rect2(corners[0], Vector2.Zero);
			foreach (Vector2 corner in corners.Skip(1)) rect = rect.Expand(corner);
			rectangles.Add(rect.Grow(4));
		}
		FocusRects = rectangles;
		var uniforms = new Godot.Collections.Array<Vector4>();
		for (int index = 0; index < 16; index++)
		{
			Rect2 rect = index < rectangles.Count ? rectangles[index] : new Rect2();
			uniforms.Add(new Vector4(rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y));
		}
		_material.SetShaderParameter("overlay_size", _shade.Size);
		_material.SetShaderParameter("focus_rects", uniforms);
		_material.SetShaderParameter("focus_count", rectangles.Count);
	}

	public override void _Input(InputEvent @event)
	{
		if (!Visible) return;
		bool advance = @event switch
		{
			InputEventMouseButton mouse => mouse.Device != -1 && mouse.ButtonIndex == MouseButton.Left && !mouse.Pressed,
			InputEventScreenTouch touch => touch.Index == 0 && !touch.Pressed && !touch.Canceled,
			InputEventKey key => !key.Pressed && !key.Echo && (key.Keycode is Key.Enter or Key.Space),
			_ => false
		};
		// Consume the whole click, including presses, so the final release cannot play a card.
		GetViewport().SetInputAsHandled();
		if (advance) AdvanceRequested?.Invoke();
	}
}
