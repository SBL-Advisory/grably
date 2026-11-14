using Grably.App.Shell;
using Grably.App.ViewModels;
using Grably.App.Views;
using Grably.Core.Settings;
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

        // Shell
        services.AddSingleton<App>();
        services.AddSingleton<ThemeController>();
        services.AddSingleton<AppCommands>();
        services.AddSingleton<TrayIconController>();
        services.AddSingleton<HotkeyController>();

        // Windows and view models
        services.AddTransient<LibraryWindow>();
        services.AddTransient<LibraryViewModel>();
        return services;
    }
}
