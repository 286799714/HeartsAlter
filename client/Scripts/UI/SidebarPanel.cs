using System.Collections.Generic;
using Godot;

namespace HeartsAlter.Scripts.UI;

/// <summary>Owns tab selection and scrolling; the parent decides which page to show.</summary>
public partial class SidebarPanel : Control
{
	[Signal] public delegate void TabSelectedEventHandler(StringName tabName);
	[Export] public StringName InitialTab { get; set; } = "CreateTab";
	[Export] public Texture2D SelectedIcon { get; set; }

	public StringName SelectedTab { get; private set; } = "";

	private readonly Dictionary<StringName, Button> _buttons = new();
	private readonly Dictionary<Button, Texture2D> _normalIcons = new();
	private ScrollContainer _scroll;

	public override void _Ready()
	{
		_scroll = GetNode<ScrollContainer>("TabScroll");
		var tabs = GetNode<VBoxContainer>("TabScroll/Tabs");
		var group = new ButtonGroup { AllowUnpress = false };
		foreach (Node child in tabs.GetChildren())
		{
			if (child is not Button button) continue;
			_buttons.Add(button.Name, button);
			_normalIcons.Add(button, button.Icon);
			button.ToggleMode = true;
			button.ButtonGroup = group;
			button.Pressed += () => SelectTab(button.Name);
		}
		// Container layout also runs after resizing, so keep the selected tab in view.
		tabs.SortChildren += EnsureSelectedTabVisible;
		SelectTab(InitialTab);
	}

	/// <summary>Selects a tab by node name, emitting only when the selection changes.</summary>
	public bool SelectTab(StringName tabName)
	{
		if (!_buttons.ContainsKey(tabName)) return false;
		foreach (var (name, button) in _buttons)
		{
			bool selected = name == tabName;
			button.SetPressedNoSignal(selected);
			// A held click also uses the pressed draw mode, so style the committed selection explicitly.
			button.ThemeTypeVariation = selected ? "SelectedTab" : "";
			button.Icon = selected && SelectedIcon != null ? SelectedIcon : _normalIcons[button];
		}
		bool changed = SelectedTab != tabName;
		SelectedTab = tabName;
		Callable.From(EnsureSelectedTabVisible).CallDeferred();
		if (changed) EmitSignal(SignalName.TabSelected, tabName);
		return true;
	}

	private void EnsureSelectedTabVisible()
	{
		if (IsInsideTree() && _buttons.TryGetValue(SelectedTab, out Button button))
			_scroll.EnsureControlVisible(button);
	}
}
