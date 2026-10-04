using System.IO;
using System.Windows;
using System.Windows.Threading;
using Lumen.App.Infrastructure;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.App.Views;
using Lumen.Core.Llm;
using Lumen.Core.Llm.Claude;
using Lumen.Core.Llm.Local;
using Lumen.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lumen.App;

/// <summary>
/// Application entry point: single-instance check, dependency injection, global error handling.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the Application lifetime; everything is disposed in OnExit.")]
public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.IsFirstInstance)
        {
            // Lumen is already in the tray: ask it to show its window and quit quietly.
            _instanceGuard.SignalFirstInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Directory.CreateDirectory(AppPaths.RoamingDirectory);
        Directory.CreateDirectory(AppPaths.LocalDirectory);

        _services = ConfigureServices();
        _logger = _services.GetRequiredService<ILogger<App>>();
        RegisterGlobalExceptionHandlers();
        _logger.LogInformation("Lumen {Version} starting on {OS}", typeof(App).Assembly.GetName().Version, Environment.OSVersion);

        SettingsStore store = _services.GetRequiredService<SettingsStore>();
        bool firstRun = !File.Exists(store.FilePath);
        store.Load();

        var coordinator = _services.GetRequiredService<AssistantCoordinator>();
        bool startedInBackground = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        coordinator.Start(firstRun);
        if (!firstRun && !startedInBackground)
        {
            coordinator.OpenAssistant();
        }

        // A second launch rings this "doorbell" from a thread-pool thread; marshal to the UI thread.
        _instanceGuard.ListenForActivation(() => Dispatcher.BeginInvoke(coordinator.OpenAssistant));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Lumen exiting");

        // Disposing the container disposes every singleton it created (tray icon, hotkeys,
        // keyboard hook, local model, HTTP client...), in reverse order of creation.
        _services?.Dispose();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Composition root: the only place that knows which concrete class implements what.
    /// Everything else receives its dependencies through its constructor.
    /// </summary>
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Debug)
            .AddDebug()
            .AddProvider(new FileLoggerProvider(AppPaths.LogDirectory)));

        // Settings
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton(sp => new SettingsStore(
            AppPaths.SettingsFile, sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton<ISettingsProvider>(sp => sp.GetRequiredService<SettingsStore>());

        // Language model backends. Each is registered under its own type (so the coordinator can
        // preload the local model) and as an ILlmBackend (so the router receives both).
        services.AddSingleton<OnnxGenAiBackend>();
        services.AddSingleton(sp => new ClaudeBackend(
            sp.GetRequiredService<ISettingsProvider>(),
            sp.GetRequiredService<SettingsStore>().GetApiKey,
            sp.GetRequiredService<ILogger<ClaudeBackend>>()));
        services.AddSingleton<ILlmBackend>(sp => sp.GetRequiredService<OnnxGenAiBackend>());
        services.AddSingleton<ILlmBackend>(sp => sp.GetRequiredService<ClaudeBackend>());
        services.AddSingleton<LlmRouter>();

        // Windows integration
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<LowLevelKeyboardHook>();
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<SelectionCaptureService>();
        services.AddSingleton<SnipService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<TrayIconService>();

        // UI
        services.AddSingleton<AssistantViewModel>();
        services.AddSingleton<AssistantWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddSingleton<Func<SettingsWindow>>(sp => sp.GetRequiredService<SettingsWindow>);
        services.AddSingleton<AssistantCoordinator>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>
    /// A tray app should survive bugs: log them, tell the user, keep running.
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        // Exceptions on the UI thread (event handlers, bindings, async void continuations).
        DispatcherUnhandledException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unhandled UI exception");
            _services?.GetService<TrayIconService>()?.ShowNotification("Lumen hit an error", args.Exception.Message, isError: true);
            args.Handled = true;
        };

        // Faulted tasks nobody awaited.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        // Anything else (background threads): cannot be recovered, but at least it is logged.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Fatal unhandled exception");
    }
}
