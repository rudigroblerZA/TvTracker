namespace TvTracker.Mobile.WinUI;

/// <summary>
/// Windows entry point, used for desktop testing of the mobile UI.
/// </summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
