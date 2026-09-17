using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts;

namespace HeartsAlter.Tests;

/// <summary>Verifies resource residency across real scene swaps without a server.</summary>
public partial class SceneResidencySmoke : Node
{
	private readonly Dictionary<string, ulong> _resourceIds = new();
	private readonly List<double> _lookupMs = new();
	private readonly List<double> _swapMs = new();
	private readonly List<double> _readyMs = new();

	public override async void _Ready()
	{
		int exitCode = 0;
		try
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			GetTree().CurrentScene = null; // The runner survives production scene changes.
			GameSession.Clear();
			var library = GetNode<ResourcePreloader>(SceneNavigation.LibraryPath);
			foreach (string path in library.GetResourceList()) Remember(path);
			// Keep only IDs, so this test cannot itself keep any loaded resource alive.
			Remember("res://assets/fonts/AlibabaPuHuiTi_Bold.ttf");
			Remember("res://assets/textures/ui/intro/generated/rgba-1642x276-df67a7afc0ea298d.png");
			Remember("res://addons/figma_importer/shader/vertex_apply_shader.gdshader");
			Remember("res://assets/resources/StandardCardResource.tres");
			foreach (string suit in new[] { "Club", "Diamond", "Heart", "Spade" })
				foreach (string rank in new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" })
					Remember($"res://assets/textures/cards/{suit}{rank}.png");
			for (int cycle = 0; cycle < 10; cycle++)
			{
				await Swap("res://scenes/Intro.tscn");
				var roomName = GetTree().CurrentScene.GetNode<LineEdit>("%RoomNameInput");
				Check(roomName.Text == "", "Old Intro input state leaked into a new scene instance");
				roomName.Text = "temporary input";
				await Swap("res://scenes/in_game/Table.tscn");
				GameSession.Clear();
				GC.Collect();
				GC.WaitForPendingFinalizers();
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				foreach (var (path, id) in _resourceIds)
				{
					Check(ResourceLoader.HasCached(path), $"Resource was evicted: {path}");
					Check(ResourceLoader.Load(path).GetInstanceId() == id, $"Resource was reloaded: {path}");
				}
			}
			await Swap("res://scenes/Lobby.tscn");
			var tutorialButton = (Button)GetTree().CurrentScene.FindChild("TutorialButton", true, false);
			tutorialButton.EmitSignal(BaseButton.SignalName.Pressed);
			await ToSignal(GetTree(), SceneTree.SignalName.SceneChanged);
			Check(GetTree().CurrentScene.SceneFilePath == "res://scenes/Tutorial.tscn", "Tutorial navigation failed");
			GetTree().CurrentScene.GetNode<Button>("%BackToLobby").EmitSignal(BaseButton.SignalName.Pressed);
			await ToSignal(GetTree(), SceneTree.SignalName.SceneChanged);
			Check(GetTree().CurrentScene is Lobby, "Tutorial return failed");
			GD.Print($"SCENE_RESIDENCY_OK resources={_resourceIds.Count}, round_trips=10, " +
				$"lookup_median_ms={Median(_lookupMs):F3}, swap_call_median_ms={Median(_swapMs):F3}, " +
				$"scene_ready_median_ms={Median(_readyMs):F3}, scene_ready_max_ms={_readyMs.Max():F3}");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError(exception.ToString());
		}
		finally
		{
			GetTree().Quit(exitCode);
		}
	}

	private void Remember(string path)
	{
		Check(ResourceLoader.HasCached(path), $"Resource was not preloaded: {path}");
		_resourceIds.Add(path, ResourceLoader.Load(path).GetInstanceId());
	}

	private async Task Swap(string path)
	{
		var library = GetNode<ResourcePreloader>(SceneNavigation.LibraryPath);
		long start = Stopwatch.GetTimestamp();
		Check(library.GetResource(path).GetInstanceId() == _resourceIds[path], "Resident scene changed");
		_lookupMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		Node previous = GetTree().CurrentScene;
		start = Stopwatch.GetTimestamp();
		Check(SceneNavigation.Change(this, path) == Error.Ok, $"Could not change scene: {path}");
		_swapMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		await ToSignal(GetTree(), SceneTree.SignalName.SceneChanged);
		_readyMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		Check(GetTree().CurrentScene.SceneFilePath == path, "Wrong scene entered");
		Check(previous == null || !IsInstanceValid(previous), "Previous scene instance was not freed");
	}

	private static double Median(List<double> values) => values.OrderBy(x => x).ElementAt(values.Count / 2);
	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
