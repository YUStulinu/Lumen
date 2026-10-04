using System.Reflection;
using System.Windows;
using Lumen.App.Infrastructure;
using Lumen.App.ViewModels;

namespace Lumen.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Saved += (_, _) => Close();
        SourceInitialized += (_, _) => WindowHelpers.ApplySystemChrome(this);

        Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "1.0.0"} · .NET {Environment.Version}";
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e) => _viewModel.SetNewApiKey(ApiKeyBox.Password);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
