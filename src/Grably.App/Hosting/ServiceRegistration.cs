using Grably.App.Capture;
using Grably.App.Editor;
using Grably.App.Shell;
using Grably.App.ViewModels;
using Grably.App.Views;
using Grably.Core.Settings;
using Grably.Platform.Capture;
using Grably.Platform.Hotkeys;
using Microsoft.Extensions.DependencyInjection;

namespace Grably.App.Hosting;

internal static class ServiceRegistration
{
    public static IServiceCollection AddGrablyApp(this IServiceCollection services)
    {
        // Core and Platform
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<ForegroundWindowTracker>(); // created on the UI thread at startup (needs its message loop)

        // Shell
        services.AddSingleton<App>();
        services.AddSingleton<ThemeController>();
        services.AddSingleton<UserNotifications>();
        services.AddSingleton<AppCommands>();
        services.AddSingleton<TrayIconController>();
        services.AddSingleton<HotkeyController>();

        // Capture and editing
        services.AddSingleton<ScreenshotService>();
        services.AddSingleton<CaptureDelivery>();
        services.AddSingleton<EditorService>();

        // Windows and view models
        services.AddTransient<LibraryWindow>();
        services.AddTransient<LibraryViewModel>();
        return services;
    }
}
