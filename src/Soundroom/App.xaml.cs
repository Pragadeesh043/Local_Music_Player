using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Soundroom;

public partial class App : Application
{
    private static readonly IntPtr PerMonitorDpiAwareV2 = new(-4);

    private Window? window;

    [ModuleInitializer]
    internal static void InitializeDpiAwareness() => SetProcessDpiAwarenessContext(PerMonitorDpiAwareV2);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => Diagnostics.Write("Unhandled application exception", args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); }
        catch (Exception error)
        {
            window = new Window
            {
                Title = "MP4A",
                Content = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap,
                    Text = Diagnostics.Report("Could not start MP4A", error) + $"\n\nLog location\n{Diagnostics.LogFile}"
                }
            };
        }
        window.Activate();
    }
}
