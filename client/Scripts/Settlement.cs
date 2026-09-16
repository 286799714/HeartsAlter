using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HeartsAlter.Scripts.Generated;

namespace HeartsAlter.Scripts;

/// <summary>The table's dismissible view of the authoritative round settlement.</summary>
public partial class Settlement : Control
{
	private sealed record PlayerRow(Control Root, Label Name, Label Score, Label Payout, Label Net, TextureRect Avatar);
	private readonly List<PlayerRow> _rows = new();
	private Control _content;
	private Label _hint;
	private Label _treatingName;
	private TextureRect _treatingAvatar;
	private Button _closeButton;
	public event Action Closed;

	public override void _Ready()
	{
		_content = GetNode<Control>("Frame 56 xIDx81_8847x");
		_hint = GetNode<Label>("%HintText");
		_treatingName = GetNode<Label>("%请客玩家昵称");
		_treatingAvatar = (TextureRect)GetNode<Control>("%请客玩家头像").FindChild("PlayerAvatar", true, false);
		_closeButton = GetNode<Button>("%CloseButton");
		_closeButton.Pressed += Close;
		foreach (string name in new[] { "First", "Second", "Third", "Fourth" })
		{
			var root = GetNode<Control>($"%{name}PlayerSettlementInfo");
			_rows.Add(new PlayerRow(root,
				(Label)root.FindChild("昵称", true, false),
				(Label)root.FindChild("红包分", true, false),
				(Label)root.FindChild("分得金币", true, false),
				(Label)root.FindChild("净收益", true, false),
				(TextureRect)root.FindChild("PlayerAvatar", true, false)));
		}
		Resized += FitContent;
		FitContent();
	}

	public void Open()
	{
		Show();
		FitContent();
		_closeButton.GrabFocus();
	}

	public void Close()
	{
		Hide();
		Closed?.Invoke();
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (!Visible || !@event.IsActionPressed("ui_cancel")) return;
		Close();
		GetViewport().SetInputAsHandled();
	}

	public void Render(MyRoomState state, string sessionId)
	{
		var players = state.players.Keys.Cast<string>().Select(id => (Id: id, Player: state.players[id]))
			.OrderByDescending(entry => entry.Player.score).ThenBy(entry => entry.Player.seat).ToList();
		for (int index = 0; index < _rows.Count; index++)
		{
			PlayerRow row = _rows[index];
			row.Root.Visible = index < players.Count;
			if (!row.Root.Visible) continue;
			var entry = players[index];
			Player player = entry.Player;
			row.Name.Text = player.name + (entry.Id == sessionId ? " (你)" : "");
			row.Name.TooltipText = $"{row.Name.Text} · 筹码 {player.chips} · {(player.nextRoundReady ? "已同意下一局" : "等待同意下一局")}";
			row.Score.Text = player.score.ToString();
			row.Payout.Text = player.payout.ToString();
			int net = player.payout - player.stake;
			row.Net.Text = net >= 0 ? $"+{net}" : net.ToString();
			row.Net.LabelSettings = (LabelSettings)row.Net.LabelSettings.Duplicate();
			row.Net.LabelSettings.FontColor = net >= 0 ? new Color("b6751c") : new Color("bd3b44");
			row.Avatar.Texture = GetAvatar(player.avatarId);
		}

		var treating = players.Where(entry => entry.Player.isTreating).ToList();
		_treatingName.Text = string.Join("、", treating.Select(entry => entry.Player.name));
		_treatingName.TooltipText = _treatingName.Text;
		_treatingName.LabelSettings = (LabelSettings)_treatingName.LabelSettings.Duplicate();
		_treatingName.LabelSettings.FontSize = treating.Count > 1 ? 24 : 32;
		_treatingAvatar.Texture = treating.Count > 0 ? GetAvatar(treating[0].Player.avatarId) : null;
		if (!state.players.TryGetValue(sessionId, out Player local))
		{
			_hint.Text = "本局已结束";
			return;
		}
		int profit = local.payout - local.stake;
		_hint.Text = local.isTreating ? "你是手气王，老老实实请客吧~"
			: profit < 0 ? $"你获得 {local.payout} 金币，小亏 {-profit} 金币"
			: $"你获得 {local.payout} 金币，净收益 {profit} 金币";
	}

	private Texture2D GetAvatar(int avatarId)
	{
		int id = avatarId is >= 1 and <= 4 ? avatarId : 1;
		var library = GetNode<ResourcePreloader>(SceneNavigation.LibraryPath);
		return (Texture2D)library.GetResource($"res://assets/textures/ui/profile_icon_{id}.jpg");
	}

	private void FitContent()
	{
		if (_content is null) return;
		// Keep the imported artwork's proportions with room for its decorative edges.
		float scale = Mathf.Min(0.667f, Mathf.Min((Size.X - 40) / 1760f, (Size.Y - 40) / 990f));
		_content.Scale = Vector2.One * Mathf.Max(0.1f, scale);
		_content.Position = (Size - new Vector2(1760, 990) * _content.Scale) / 2;
	}
}
