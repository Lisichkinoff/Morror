using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace Morror
{
    internal static class WindowFrame
    {
        internal static void Bind(Window window, Border frame, Button minimize, Button maximize, Button close)
        {
            Bind(window, frame, close);
            minimize.Click += (sender, args) => SystemCommands.MinimizeWindow(window);
            maximize.Click += (sender, args) =>
            {
                if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            };
        }

        internal static void Bind(Window window, Border frame, Button close)
        {
            // Match the content clip to the HWND corner curve.
            double radius = WindowChrome.GetWindowChrome(window).CornerRadius.TopLeft / 2;
            Action updateClip = () => frame.Clip = window.WindowState == WindowState.Normal
                ? new RectangleGeometry(new Rect(frame.RenderSize), radius, radius)
                : null;
            frame.SizeChanged += (sender, args) => updateClip();
            window.StateChanged += (sender, args) => updateClip();
            close.Click += (sender, args) => window.Close();
        }
    }
}
