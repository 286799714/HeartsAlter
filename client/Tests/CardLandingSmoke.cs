using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Tests;

/// <summary>
/// Rendered regression for the flight/PlayArea/collection hand-offs. Run with
/// --resolution 1280x720 and a real renderer (not --headless).
/// </summary>
public partial class CardLandingSmoke : Node
{
	private readonly Rect2I _trickRegion = new(470, 140, 310, 310);
	private int _comparisons;
	private string _captureDirectory;

	public override async void _Ready()
	{
		int exitCode = 0;
		bool originalSnapping = GetViewport().GuiSnapControlsToPixels;
		try
		{
			Check(DisplayServer.GetName() != "headless", "This check requires a real renderer.");
			Check(GetViewport().GetVisibleRect().Size.IsEqualApprox(new Vector2(1280, 720)), "Run at 1280x720.");
			_captureDirectory = OS.GetCmdlineUserArgs().FirstOrDefault(value => value.StartsWith("--capture-dir="))?["--capture-dir=".Length..];
			foreach (bool snapping in new[] { true, false })
			{
				GetViewport().GuiSnapControlsToPixels = snapping;
				using var reference = new Image();
				for (int leader = 0; leader < 5; leader++)
				{
					var table = GD.Load<PackedScene>("res://scenes/in_game/Table.tscn").Instantiate<Table>();
					table.UseNetworkSession = false;
					AddChild(table);
					if (leader == 4)
					{
						// Exercise explicit scene Z overrides during flight as well as collection.
						table.GetNode<PlayArea>("InGame/LeftPlayArea").ZIndex = 3;
						table.GetNode<PlayArea>("InGame/MainPlayArea").ZIndex = -2;
					}
					var layer = table.GetNode<AnimationLayer>("InGame/AnimationLayer");
					layer.PlayFlightDuration = 0.3f;
					layer.PlayClockwiseTurns = 0;
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					string prefix = $"snap{snapping}-leader{leader}";
					for (int play = 0; play < 4; play++)
					{
						int seat = (leader + play) % 4;
						await CheckLanding(table, layer, seat, $"{prefix}-seat{seat}");
					}
					using (Image trick = ReadTrickImage())
					{
						if (leader == 0) reference.CopyFrom(trick);
						else if (leader < 4) Compare(reference, trick, prefix + "-fixed-play-area-order");
					}
					await CheckCollection(table, prefix);
					table.QueueFree();
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					GD.Print($"CARD_LANDING_CASE_OK: {prefix}");
				}
			}
			GD.Print($"CARD_LANDING_SMOKE_OK: 40 landings, 10 collections, {_comparisons} frame comparisons");
		}
		catch (Exception exception) { exitCode = 1; GD.PushError(exception.ToString()); }
		finally
		{
			GetViewport().GuiSnapControlsToPixels = originalSnapping;
			GetTree().Quit(exitCode);
		}
	}

	private async Task CheckLanding(Table table, AnimationLayer layer, int seat, string label)
	{
		string[] names = { "MainPlayArea", "LeftPlayArea", "OppositePlayArea", "RightPlayArea" };
		string[] ids = { "Club2", "Diamond3", "Heart4", "Spade5" };
		var area = table.GetNode<PlayArea>("InGame/" + names[seat]);
		var card = GD.Load<PackedScene>("res://scenes/in_game/Card.tscn").Instantiate<CardControl>();
		layer.AddChild(card);
		card.Setup(CardRules.Parse(ids[seat]), true);
		card.ZIndex = 7; // Simulate the ordering inherited from a hand.
		CardPose2D pose = area.GetReceivePose(card);
		bool landed = false;
		// Hold at the endpoint to isolate the hand-off. Flying and landed cards
		// must use the same play-area order even where other cards overlap.
		Check(layer.PlayCardToPose(card, pose, pose, area, played =>
		{
			area.ReceiveCard(played);
			landed = true;
		}), "Play flight did not start.");
		Image before = null;
		try
		{
			int frames = 0;
			while (!landed)
			{
				Check(++frames < 600, "Flight did not finish.");
				await NextRenderedFrame();
				using Image frame = ReadTrickImage();
				if (before is not null)
					Compare(before, frame, label + (landed ? "-landing" : $"-flight{frames}"));
				else
				{
					before = (Image)frame.Duplicate();
				}
			}
			Vector2 areaPivot = area.GetGlobalTransformWithCanvas() * area.GetCombinedPivotOffset();
			Vector2 cardPivot = card.GetGlobalTransformWithCanvas() * card.GetCombinedPivotOffset();
			Vector2 cardCenter = card.GetGlobalTransformWithCanvas() * (card.Size * 0.5f);
			Check(cardPivot.IsEqualApprox(areaPivot),
				$"{label}: canvas card pivot {cardPivot} does not match play-area pivot {areaPivot}.");
			Check(cardCenter.IsEqualApprox(areaPivot),
				$"{label}: visible card center {cardCenter} does not match play-area pivot {areaPivot}.");
			for (int index = 0; index < 3; index++)
			{
				await NextRenderedFrame();
				using Image frame = ReadTrickImage();
				Compare(before, frame, $"{label}-after{index}");
			}
		}
		finally { before?.Dispose(); }
	}

	private async Task CheckCollection(Table table, string label)
	{
		using Image before = ReadTrickImage();
		table.TrickCollectDelay = 1.0f;
		Check(table.CollectTrick(0, 0), "Collection did not start.");
		for (int index = 0; index < 3; index++)
		{
			await NextRenderedFrame();
			using Image frame = ReadTrickImage();
			Compare(before, frame, $"{label}-collection{index}");
		}
		table.ResetLocalPresentation();
	}

	private async Task NextRenderedFrame() =>
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

	private Image ReadTrickImage()
	{
		using Image image = GetViewport().GetTexture().GetImage();
		return image.GetRegion(_trickRegion);
	}

	private void Compare(Image before, Image after, string label)
	{
		Check(before is not null, "Missing in-flight frame.");
		int changed = 0;
		for (int y = 0; y < before.GetHeight(); y++)
			for (int x = 0; x < before.GetWidth(); x++)
			{
				Color a = before.GetPixel(x, y);
				Color b = after.GetPixel(x, y);
				float difference = Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Abs(a.B - b.B));
				if (difference > 0.02f) changed++;
			}
		if (_captureDirectory is not null)
		{
			System.IO.Directory.CreateDirectory(_captureDirectory);
			before.SavePng(System.IO.Path.Combine(_captureDirectory, label + "-before.png"));
			after.SavePng(System.IO.Path.Combine(_captureDirectory, label + "-after.png"));
		}
		Check(changed == 0, $"{label}: {changed} pixels changed during the hand-off.");
		_comparisons++;
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
