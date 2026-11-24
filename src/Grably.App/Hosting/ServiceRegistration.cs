using Grably.App.Capture;
using Grably.App.Editor;
using Grably.App.Library;
using Grably.App.Shell;
using Grably.App.Text;
using Grably.App.ViewModels;
using Grably.App.Views;
using Grably.Core.Settings;
using Grably.Platform.Capture;
using Grably.Platform.Hotkeys;
using Grably.Platform.Shell;
using Grably.Platform.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Grably.App.Hosting;

internal static class ServiceRegistration
{
    public static IServiceCollection AddGrablyApp(this IServiceCollection services)
    {
        // Core and Platform
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<StartupRegistration>();
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
        services.AddSingleton<LibraryService>();
        services.AddSingleton<LibraryActions>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<TextCaptureService>();
        services.AddSingleton<TextIndexer>();
        services.AddSingleton<ScrollingCaptureService>();
        services.AddSingleton<Recording.RecordingController>();
        services.AddSingleton<Trim.TrimService>();

        // Windows and view models
        services.AddTransient<LibraryWindow>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<SettingsViewModel>();
        return services;
    }
}
