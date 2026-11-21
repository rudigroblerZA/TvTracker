using Android.App;
using Android.Runtime;

namespace TvTracker.Mobile;

// Plain http only where the app talks to the AppHost on this machine: a debug build, or one built with no host
// (LOCAL_BACKEND, from MobileApp.csproj). A release for a deployment is https throughout.
#if DEBUG || LOCAL_BACKEND
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
