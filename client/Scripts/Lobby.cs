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
	private const int PanelUnlockClickCount = 7;
	private const ulong PanelUnlockClickIntervalMs = 1000;
	private ColyseusLobbyAdapter _adapter;
	private LineEdit _hostInput;
	private LineEdit _portInput;
	private OptionButton _endpointPreset;
	private Label _status;
	private Button _enterGameButton;
	private Control _enterGameArt;
	private Control _connectionPanel;
	private int _panelUnlockClicks;
	private ulong _lastPanelUnlockClick;
	private bool _connecting;
	private bool _transitioning;

	public override void _Ready()
	{
		BindUi();
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
			ShowStatus(error);
			return;
		}
		SetConnecting(true);
		ShowStatus($"正在连接 {endpoint}…");
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
		if (IsInsideTree()) ShowStatus($"无法打开大厅：{sceneError}");
	}

	private void HandleError(string message)
	{
		if (IsInsideTree()) ShowStatus($"连接失败：{message}");
	}

	private void ShowStatus(string message)
	{
		_status.Text = message;
		_status.Visible = !string.IsNullOrEmpty(message);
	}

	private void ApplyPreset(long index)
	{
		_hostInput.Text = index == 0 ? DefaultHost : "127.0.0.1";
		_portInput.Text = DefaultPort;
	}

	private void HandlePanelUnlock()
	{
		if (_connectionPanel.Visible) return;
		ulong now = Time.GetTicksMsec();
		if (now - _lastPanelUnlockClick > PanelUnlockClickIntervalMs) _panelUnlockClicks = 0;
		_lastPanelUnlockClick = now;
		if (++_panelUnlockClicks < PanelUnlockClickCount) return;
		_panelUnlockClicks = 0;
		_connectionPanel.Show();
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

	private void BindUi()
	{
		_hostInput = GetNode<LineEdit>("%HostInput");
		_portInput = GetNode<LineEdit>("%PortInput");
		_endpointPreset = GetNode<OptionButton>("%EndpointPreset");
		_enterGameButton = GetNode<Button>("%EnterGameButton");
		_enterGameArt = GetNode<Control>("进入游戏按钮");
		_connectionPanel = GetNode<Control>("ConnectionPanel");
		_connectionPanel.Hide();
		_status = GetNode<Label>("%ConnectionStatus");
		_endpointPreset.ItemSelected += ApplyPreset;
		_enterGameButton.Pressed += () => _ = ConnectAsync();
		_hostInput.TextSubmitted += text => _ = ConnectAsync();
		_portInput.TextSubmitted += text => _ = ConnectAsync();
		GetNode<Button>("%ConnectionPanelUnlock").Pressed += HandlePanelUnlock;
		GetNode<Button>("%HidePanelButton").Pressed += () =>
		{
			_connectionPanel.Hide();
			_panelUnlockClicks = 0;
		};
		if (Uri.TryCreate(GameSession.ServerEndpoint, UriKind.Absolute, out var previous))
		{
			_hostInput.Text = previous.Scheme == "wss" ? $"wss://{previous.Host}" : previous.Host;
			_portInput.Text = previous.Port.ToString();
		}
	}

	private void SetConnecting(bool connecting)
	{
		_connecting = connecting;
		_enterGameButton.Disabled = connecting;
		_enterGameArt.Modulate = connecting ? new Color(1, 1, 1, 0.55f) : Colors.White;
		_endpointPreset.Disabled = connecting;
		_hostInput.Editable = !connecting;
		_portInput.Editable = !connecting;
	}
}
