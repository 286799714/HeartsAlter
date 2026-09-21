using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HeartsAlter.Scripts;
using HeartsAlter.Scripts.Tutorial;
using HeartsAlter.Scripts.UI;

namespace HeartsAlter.Tests;

/// <summary>Exercises real sidebar input, page binding and overflow without a server.</summary>
public partial class SidebarSmoke : Node
{
	public override async void _Ready()
	{
		int exitCode = 0;
		try
		{
			GameSession.Clear();
			var intro = GD.Load<PackedScene>("res://scenes/Intro.tscn").Instantiate<Control>();
			AddChild(intro);
			await Frames();
			var sidebar = intro.GetNode<SidebarPanel>("%Sidebar");
			var scroll = sidebar.GetNode<ScrollContainer>("TabScroll");
			CheckSelection(sidebar, "TutorialTab");
			Check(intro.GetNode<Control>("%TutorialPage").Visible, "Intro must open on the tutorial page");
			Check(!scroll.GetVScrollBar().Visible, "Four tabs should fit without a scrollbar");
			int changes = 0;
			sidebar.TabSelected += _ => changes++;
			await Capture("sidebar-tutorial-default");
			foreach (string tab in new[] { "HistoryTab", "JoinTab", "CreateTab", "TutorialTab" })
			{
				await Click(Tab(sidebar, tab));
				CheckSelection(sidebar, tab);
				foreach (string page in new[] { "CreatePage", "JoinPage", "HistoryPage", "TutorialPage" })
					Check(intro.GetNode<Control>("%" + page).Visible == (page == tab.Replace("Tab", "Page")),
						$"Page visibility did not follow {tab}: {page}");
			}
			await Click(Tab(sidebar, "TutorialTab"));
			CheckSelection(sidebar, "TutorialTab");
			Check(changes == 4, "Clicking the selected tab emitted a duplicate change");
			Check(!sidebar.SelectTab("MissingTab") && sidebar.SelectedTab == "TutorialTab", "Unknown tab changed selection");
			sidebar.SelectTab("JoinTab");
			await Frames();
			Check(intro.GetNode<Control>("%JoinPage").Visible, "Programmatic selection did not open its page");
			await Capture("sidebar-join");

			// A shorter instance must scroll, and page changes must reuse that instance.
			sidebar.Size = new Vector2(295, 300);
			await Frames();
			Check(scroll.GetVScrollBar().Visible, "Short sidebar did not enable scrolling");
			sidebar.SelectTab("HistoryTab");
			await Frames();
			CheckFullyVisible(scroll, Tab(sidebar, "HistoryTab"));
			int position = scroll.ScrollVertical;
			await Click(Tab(sidebar, "JoinTab"));
			Check(intro.GetNode<Control>("%JoinPage").Visible && scroll.ScrollVertical == position,
				"Changing pages reset the sidebar scroll position");
			intro.QueueFree();
			await Frames();

			// Extra buttons authored in the scene are automatically bound on instantiation.
			var packed = GD.Load<PackedScene>("res://scenes/ui/SidebarPanel.tscn");
			var many = packed.Instantiate<SidebarPanel>();
			var tabs = many.GetNode<VBoxContainer>("TabScroll/Tabs");
			for (int index = 0; index < 9; index++)
			{
				var extra = (Button)tabs.GetChild<Button>(1).Duplicate();
				extra.Name = "ExtraTab" + index;
				extra.Text = "扩展页签";
				tabs.AddChild(extra);
			}
			many.InitialTab = "ExtraTab8";
			many.Position = new Vector2(80, 40);
			AddChild(many);
			var second = packed.Instantiate<SidebarPanel>();
			second.Position = new Vector2(460, 40);
			AddChild(second);
			await Frames();
			scroll = many.GetNode<ScrollContainer>("TabScroll");
			Check(scroll.GetVScrollBar().Visible && scroll.ScrollVertical > 0, "Overflow or initial offscreen selection failed");
			CheckFullyVisible(scroll, Tab(many, "ExtraTab8"));
			CheckSelection(many, "ExtraTab8");
			CheckSelection(second, "CreateTab");
			many.SelectTab("TutorialTab");
			await Frames();
			Check(scroll.ScrollVertical == 0, "Selecting the first tab did not scroll back to the top");
			await Wheel(scroll, MouseButton.WheelDown);
			Check(scroll.ScrollVertical > 0, "Mouse wheel over a tab did not scroll down");
			CheckSelection(many, "TutorialTab");
			await Wheel(scroll, MouseButton.WheelUp);
			Check(scroll.ScrollVertical == 0, "Mouse wheel did not scroll up");
			Tab(many, "ExtraTab8").GrabFocus();
			await Frames();
			CheckFullyVisible(scroll, Tab(many, "ExtraTab8"));
			await Click(Tab(many, "ExtraTab8"));
			CheckSelection(many, "ExtraTab8");
			CheckSelection(second, "CreateTab");
			await Capture("sidebar-overflow");
			many.QueueFree();
			second.QueueFree();
			await Frames();

			// Keep the runner alive through the actual tutorial entry and return path.
			GetTree().CurrentScene = null;
			Check(SceneNavigation.Change(this, "res://scenes/Intro.tscn") == Error.Ok, "Could not open Intro");
			await ToSignal(GetTree(), SceneTree.SignalName.SceneChanged);
			await Frames();
			intro = (Control)GetTree().CurrentScene;
			sidebar = intro.GetNode<SidebarPanel>("%Sidebar");
			await Click(Tab(sidebar, "TutorialTab"));
			CheckSelection(sidebar, "TutorialTab");
			var lessons = intro.GetNode<VBoxContainer>("%TutorialList").GetChildren().OfType<Control>()
				.Where(row => row.Visible).ToArray();
			Check(lessons.Length == TutorialCatalog.Lessons.Length, "Tutorial list does not match the lesson catalog");
			await Capture("sidebar-tutorial");
			await Click((Button)lessons[0].FindChild("StartTutorial", true, false));
			Check(GetTree().CurrentScene is TutorialController { Phase: TutorialPhase.Guiding, LessonIndex: 0, StepIndex: 0 },
				$"Tutorial entry did not start the selected lesson directly: {GetTree().CurrentScene?.SceneFilePath}, " +
				$"phase={(GetTree().CurrentScene as TutorialController)?.Phase}");
			await Click(GetTree().CurrentScene.GetNode<BaseButton>("Game/Table/TableActions/Quit/ReturnToLobbyButton"));
			Check(GetTree().CurrentScene.SceneFilePath == "res://scenes/Intro.tscn", "Tutorial did not return to Intro");
			CheckSelection(GetTree().CurrentScene.GetNode<SidebarPanel>("%Sidebar"), "TutorialTab");
			Check(GetTree().CurrentScene.GetNode<Control>("%TutorialPage").Visible, "Return lost the tutorial page");
			GD.Print("SIDEBAR_SMOKE_OK: four pages, selection, scrolling, focus, overflow, tutorial entry and return");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError(exception.ToString());
		}
		GetTree().Quit(exitCode);
	}

	private static Button Tab(SidebarPanel sidebar, string name) => sidebar.GetNode<Button>("TabScroll/Tabs/" + name);

	private static void CheckSelection(SidebarPanel sidebar, string name)
	{
		Check(sidebar.SelectedTab == name, "Wrong selected tab");
		foreach (Button button in sidebar.GetNode("TabScroll/Tabs").GetChildren().OfType<Button>())
		{
			Check(button.ButtonPressed == (button.Name == name), "Selection must be exclusive");
			Check((button.Icon == sidebar.SelectedIcon) == button.ButtonPressed, "Icon did not follow selection");
		}
		Check(Tab(sidebar, name).GetThemeStylebox("pressed") is StyleBoxTexture style &&
			style.Texture.ResourcePath == "res://assets/textures/ui/intro/selected.png", "Wrong selection artwork");
	}

	private static void CheckFullyVisible(ScrollContainer scroll, Control control)
	{
		Rect2 bounds = scroll.GetGlobalRect();
		Rect2 item = control.GetGlobalRect();
		Check(item.Position.Y >= bounds.Position.Y - 1 && item.End.Y <= bounds.End.Y + 1,
			"Selected or focused tab is outside the visible scroll area");
	}

	private async Task Click(Control control)
	{
		Vector2 point = control.GetGlobalTransform() * (control.Size / 2);
		await Mouse(point, MouseButton.Left);
	}

	private async Task Wheel(ScrollContainer scroll, MouseButton direction)
	{
		Vector2 point = scroll.GetGlobalTransform() * new Vector2(100, 50);
		await Mouse(point, direction);
	}

	private async Task Mouse(Vector2 point, MouseButton button)
	{
		foreach (bool pressed in new[] { true, false })
			GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point,
				ButtonIndex = button, Pressed = pressed, Factor = 1 }, true);
		await Frames();
	}

	private async Task Frames()
	{
		for (int frame = 0; frame < 4; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private async Task Capture(string name)
	{
		string directory = OS.GetCmdlineUserArgs().FirstOrDefault(arg => arg.StartsWith("--capture-dir="))?[14..];
		if (string.IsNullOrEmpty(directory)) return;
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(directory, name + ".png"));
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
