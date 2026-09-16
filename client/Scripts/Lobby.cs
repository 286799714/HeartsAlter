using System;
using System.Threading.Tasks;
using Godot;

namespace HeartsAlter.Scripts;

/// <summary>Connection screen. Hands the synchronized lobby connection to Intro.</summary>
public partial class Lobby : Control
{
	[Export] public string Endpoint = ColyseusLobbyAdapter.DefaultEndpoint;
	private const string DefaultHost = "ddns.maydaymemory.com";
	private const string DefaultPort = "2567";
	private ColyseusLobbyAdapter _adapter;
	private LineEdit _playerName;
	private LineEdit _hostInput;
	private LineEdit _portInput;
	private OptionButton _endpointPreset;
	private Label _status;
	private Button _connectButton;
	private Button _tutorialButton;
	private bool _connecting;
	private bool _transitioning;

	public override void _Ready()
	{
		BuildUi();
		_adapter = new ColyseusLobbyAdapter();
		_adapter.Error += HandleError;
	}

	public override void _ExitTree()
	{
		_adapter.Error -= HandleError;
		if (GameSession.LobbyAdapter != _adapter) _ = _adapter.DisconnectAsync();
	}

	private async Task ConnectAsync()
	{
		if (_connecting || _transitioning) return;
		if (!TryBuildEndpoint(out string endpoint, out string error))
		{
			_status.Text = error;
			return;
		}
		_adapter.PlayerName = _playerName.Text.Trim();
		SetConnecting(true);
		_status.Text = $"正在连接 {endpoint}…";
		bool connected = await _adapter.ConnectAsync(endpoint);
		if (!IsInstanceValid(this) || !IsInsideTree())
		{
			await _adapter.DisconnectAsync();
			return;
		}
		SetConnecting(false);
		if (!connected) return;
		GameSession.ServerEndpoint = endpoint;
		GameSession.LobbyAdapter = _adapter;
		_transitioning = true;
		Error sceneError = SceneNavigation.Change(this, "res://scenes/Intro.tscn");
		if (sceneError == Error.Ok) return;
		_transitioning = false;
		GameSession.LobbyAdapter = null;
		await _adapter.DisconnectAsync();
		if (IsInsideTree()) _status.Text = $"无法打开大厅：{sceneError}";
	}

	private void HandleError(string message)
	{
		if (IsInsideTree()) _status.Text = $"连接失败：{message}";
	}

	private void ApplyPreset(long index)
	{
		_hostInput.Text = index == 0 ? DefaultHost : "127.0.0.1";
		_portInput.Text = DefaultPort;
	}

	private bool TryBuildEndpoint(out string endpoint, out string error)
	{
		string host = _hostInput.Text.Trim();
		string scheme = host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
		if (host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) host = host[5..];
		if (host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) host = host[6..];
		host = host.TrimEnd('/');
		endpoint = string.Empty;
		if (host.Length == 0)
		{
			error = "请输入服务器 IP 或域名";
			return false;
		}
		if (!int.TryParse(_portInput.Text.Trim(), out int port) || port < 1 || port > 65535)
		{
			error = "端口号必须是 1-65535 之间的整数";
			return false;
		}
		endpoint = $"{scheme}://{host}:{port}";
		error = string.Empty;
		return true;
	}

	private void BuildUi()
	{
		var margin = new MarginContainer();
		margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		margin.AddThemeConstantOverride("margin_left", 72);
		margin.AddThemeConstantOverride("margin_right", 72);
		margin.AddThemeConstantOverride("margin_top", 42);
		margin.AddThemeConstantOverride("margin_bottom", 42);
		AddChild(margin);
		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 14);
		margin.AddChild(column);
		var title = new Label { Text = "红心大战 · 连接服务器" };
		title.AddThemeFontSizeOverride("font_size", 30);
		column.AddChild(title);
		_tutorialButton = new Button { Name = "TutorialButton", Text = "教学关", CustomMinimumSize = new Vector2(0, 44) };
		_tutorialButton.Pressed += () =>
		{
			if (!_connecting && !_transitioning) SceneNavigation.Change(this, "res://scenes/Tutorial.tscn");
		};
		column.AddChild(_tutorialButton);
		var endpointRow = new HBoxContainer();
		endpointRow.AddChild(new Label { Text = "服务器" });
		_hostInput = new LineEdit { Name = "HostInput", Text = DefaultHost, PlaceholderText = "IP / 域名", CustomMinimumSize = new Vector2(250, 0) };
		endpointRow.AddChild(_hostInput);
		endpointRow.AddChild(new Label { Text = ":" });
		_portInput = new LineEdit { Name = "PortInput", Text = DefaultPort, MaxLength = 5, PlaceholderText = "端口", CustomMinimumSize = new Vector2(90, 0) };
		endpointRow.AddChild(_portInput);
		_endpointPreset = new OptionButton { CustomMinimumSize = new Vector2(280, 0) };
		_endpointPreset.AddItem("ddns.maydaymemory.com:2567（默认）");
		_endpointPreset.AddItem("127.0.0.1:2567");
		_endpointPreset.ItemSelected += ApplyPreset;
		endpointRow.AddChild(_endpointPreset);
		_connectButton = new Button { Name = "ConnectButton", Text = "连接大厅" };
		_connectButton.Pressed += () => _ = ConnectAsync();
		endpointRow.AddChild(_connectButton);
		column.AddChild(endpointRow);
		var profile = new HBoxContainer();
		profile.AddChild(new Label { Text = "昵称" });
		_playerName = new LineEdit { Name = "PlayerNameInput", Text = GameSession.Profile?.Name ?? "玩家 1", MaxLength = 24,
			PlaceholderText = "首次建档昵称", CustomMinimumSize = new Vector2(220, 0) };
		profile.AddChild(_playerName);
		column.AddChild(profile);
		_status = new Label { Name = "ConnectionStatus", Text = "连接成功后进入大厅" };
		column.AddChild(_status);
		if (Uri.TryCreate(GameSession.ServerEndpoint, UriKind.Absolute, out var previous))
		{
			_hostInput.Text = previous.Scheme == "wss" ? $"wss://{previous.Host}" : previous.Host;
			_portInput.Text = previous.Port.ToString();
		}
	}

	private void SetConnecting(bool connecting)
	{
		_connecting = connecting;
		_connectButton.Disabled = connecting;
		_tutorialButton.Disabled = connecting;
		_endpointPreset.Disabled = connecting;
		_hostInput.Editable = !connecting;
		_portInput.Editable = !connecting;
		_playerName.Editable = !connecting;
	}
}
