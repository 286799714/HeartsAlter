using System;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts.InGame;
using HeartsAlter.Scripts.InGame.Card;

namespace HeartsAlter.Tests;

/// <summary>Resize the real window with cards at rest, in flight, and waiting for a flip.</summary>
public partial class PlayAreaResizeSmoke : Node
{
	private static readonly string[] Areas = { "MainPlayArea", "LeftPlayArea", "OppositePlayArea", "RightPlayArea" };
	private static readonly string[] Hands = { "MainHandLayout", "OtherHandLayout", "OtherHandLayout2", "OtherHandLayout3" };
	private static readonly Vector2I[] Sizes = { new(1280, 720), new(1280, 900), new(1600, 720), new(960, 720) };

	public override async void _Ready()
	{
		int exitCode = 0;
		try
		{
			Check(DisplayServer.GetName() != "headless", "This check requires a real window.");
			foreach (bool snapping in new[] { true, false })
			{
				GetViewport().GuiSnapControlsToPixels = snapping;
				var table = GD.Load<PackedScene>("res://scenes/in_game/Table.tscn").Instantiate<Table>();
				table.UseNetworkSession = false;
				AddChild(table);
				await ResizeWindow(Sizes[0]);
				var layer = table.GetNode<AnimationLayer>("InGame/AnimationLayer");
				layer.PlayFlightDuration = 0.4f;
				layer.PlayFlipDuration = 1.4f;
				layer.PlayClockwiseTurns = 0;

				foreach (string name in Areas)
				{
					var area = table.GetNode<PlayArea>("InGame/" + name);
					area.ReceiveCard(NewCard(layer));
				}
				foreach (Vector2I size in Sizes)
				{
					await ResizeWindow(size);
					foreach (string name in Areas) CheckResting(table.GetNode<PlayArea>("InGame/" + name));
				}
				foreach (string name in Areas) table.GetNode<PlayArea>("InGame/" + name).ClearCard();
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

				for (int seat = 0; seat < 4; seat++)
				{
					await ResizeWindow(Sizes[0]);
					var area = table.GetNode<PlayArea>("InGame/" + Areas[seat]);
					CardControl card = NewCard(layer);
					Control hand = table.GetNode<Control>("InGame/" + Hands[seat]);
					if (hand is MainHandLayout main) main.ReceiveCard(card);
					else ((OtherHandLayout)hand).ReceiveCard(card);
					await Delay(0.4);
					CardPose2D? beforeHandoff = null;
					void CaptureHandoff() => beforeHandoff = new(CardPose2D.GetRenderedCanvasTransform(card), card.Size, true);
					layer.CardAnimationCompleted += CaptureHandoff;
					bool started = hand is MainHandLayout local
						? local.TryPlayCard(0)
						: ((OtherHandLayout)hand).TryPlayCard(0, CardRules.Parse("Diamond9"));
					Check(started, "Could not start a real hand-to-area play.");
					await Delay(0.08);
					await ResizeWindow(Sizes[1]);
					if (seat > 0)
					{
						// The flight ends before the flip: the card still belongs to the carrier.
						await Delay(0.5);
						Check(layer.IsAnimating && card.GetParent() != area, "Missing flip-wait phase.");
						await ResizeWindow(Sizes[2]);
						CheckPose(new(CardPose2D.GetRenderedCanvasTransform(card), card.Size, true), area.GetReceivePose(card),
							$"{area.Name}: resize during flip wait");
					}
					ulong start = Time.GetTicksMsec();
					while (layer.IsAnimating)
					{
						Check(Time.GetTicksMsec() - start < 5000, "Flight did not finish.");
						await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					}
					layer.CardAnimationCompleted -= CaptureHandoff;
					Check(beforeHandoff.HasValue, "Missing pre-handoff pose.");
					CheckPose(beforeHandoff.Value, area.GetReceivePose(card), $"{area.Name}: flight endpoint after resize");
					CheckResting(area);
					await ResizeWindow(Sizes[3]);
					CheckResting(area);
					area.ClearCard();
				}
				table.QueueFree();
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				GD.Print($"PLAY_AREA_RESIZE_CASE_OK: snapping={snapping}, four seats, four aspect ratios");
			}
			GD.Print("PLAY_AREA_RESIZE_SMOKE_OK: resting cards, live flight endpoints, flip waits and hand-offs");
		}
		catch (Exception exception) { exitCode = 1; GD.PushError(exception.ToString()); }
		finally { GetTree().Quit(exitCode); }
	}

	private CardControl NewCard(Node parent)
	{
		var card = GD.Load<PackedScene>("res://scenes/in_game/Card.tscn").Instantiate<CardControl>();
		parent.AddChild(card);
		card.Setup(CardRules.Parse("Diamond9"), true);
		return card;
	}

	private async Task ResizeWindow(Vector2I size)
	{
		GetWindow().Size = size;
		for (int frame = 0; frame < 4; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Check(GetWindow().Size == size, $"Window did not resize to {size}.");
	}

	private async Task Delay(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

	private static void CheckResting(PlayArea area)
	{
		CardControl card = area.PlayedCard;
		Check(card.Size.X <= area.Size.X + 0.01f && card.Size.Y <= area.Size.Y + 0.01f,
			$"{area.Name}: card {card.Size} exceeds area {area.Size}.");
		Check(Mathf.Abs(card.Size.X - area.Size.X) < 0.01f || Mathf.Abs(card.Size.Y - area.Size.Y) < 0.01f,
			$"{area.Name}: card {card.Size} should fill one dimension of area {area.Size}.");
		Check(Mathf.IsEqualApprox(card.Size.X / card.Size.Y, CardControl.DefaultWidth / CardControl.DefaultHeight),
			$"{area.Name}: fitting the play area distorted the card's aspect ratio.");
		Vector2 expected = area.GetGlobalTransformWithCanvas() * area.GetCombinedPivotOffset();
		Vector2 center = card.GetGlobalTransformWithCanvas() * (card.Size * 0.5f);
		Vector2 pivot = card.GetGlobalTransformWithCanvas() * card.GetCombinedPivotOffset();
		Check(center.DistanceTo(expected) < 0.01f && pivot.DistanceTo(expected) < 0.01f,
			$"{area.Name}: resting card center {center}, pivot {pivot}, expected {expected} after resize.");
	}

	private static void CheckPose(CardPose2D actual, CardPose2D expected, string label)
	{
		foreach (Vector2 corner in new[] { Vector2.Zero, Vector2.Right, Vector2.Down, Vector2.One })
		{
			Vector2 a = actual.CanvasTransform * (actual.Size * corner);
			Vector2 b = expected.CanvasTransform * (expected.Size * corner);
			Check(a.DistanceTo(b) < 0.01f, $"{label}: corner {corner} at {a}, expected {b}.");
		}
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
