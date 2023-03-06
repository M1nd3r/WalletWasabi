using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WalletWasabi.Fluent;

public static class Diagnostics
{
	private static readonly Dictionary<TopLevel, DiagnosticsWindow> Open = new ();

	public static void AttachDiagnostics(this TopLevel root)
	{
		AttachDiagnostics(root, new(Key.F7));
	}

	public static void AttachDiagnostics(this TopLevel root, KeyGesture gesture)
	{
		void Handler(object? sender, KeyEventArgs args)
		{
			if (gesture.Matches(args))
			{
				if (Open.TryGetValue(root, out var window))
				{
					window.Activate();
				}
				else
				{
					window = new DiagnosticsWindow(root)
					{
						Root = root,
						Width = 300,
						Height = 300,
						WindowStartupLocation = WindowStartupLocation.Manual,
						WindowState = WindowState.Normal,
						Position = new PixelPoint(0, 0)
					};

					window.Closed += WindowClosed;
					Open.Add(root, window);

					window.Show();
				}
			}
		}

		root.AddHandler(InputElement.KeyDownEvent, Handler, RoutingStrategies.Tunnel);
	}

	private static void WindowClosed(object? sender, EventArgs e)
	{
		var window = (DiagnosticsWindow)sender!;
		Open.Remove(window.Root!);
		window.Closed -= WindowClosed;
	}
}
