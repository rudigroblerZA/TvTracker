using Android.App;
using Android.Runtime;

namespace TvTracker.Mobile;

// Plain http only in a debug build, which talks to the AppHost on this machine; a release is https throughout.
#if DEBUG
[Application(UsesCleartextTraffic = true)]
#else
[Application(UsesCleartextTraffic = false)]
#endif
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
