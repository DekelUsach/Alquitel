using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alquitel.Mobile.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    [ObservableProperty] private string _connectionString = string.Empty;
    [ObservableProperty] private string _pollinationsKey = string.Empty;
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private bool _aiConfigured;

    [RelayCommand]
    public Task InitializeAsync()
    {
        ConnectionString = AppConfig.ConnectionString;
        PollinationsKey = AppConfig.PollinationsApiKey ?? string.Empty;
        AiConfigured = !string.IsNullOrWhiteSpace(AppConfig.PollinationsApiKey);
        IsDarkTheme = Application.Current?.UserAppTheme != AppTheme.Light;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            await ShowAlertAsync("Conexión", "La cadena de conexión no puede quedar vacía.");
            return;
        }
        await AppConfig.SaveConnectionStringAsync(ConnectionString);
        await ShowAlertAsync("Conexión", "Cadena de conexión guardada.");
    }

    [RelayCommand]
    private async Task SaveAiKeyAsync()
    {
        await AppConfig.SavePollinationsKeyAsync(PollinationsKey);
        AiConfigured = !string.IsNullOrWhiteSpace(AppConfig.PollinationsApiKey);
        await ShowAlertAsync("IA", AiConfigured
            ? "API key guardada. El análisis de pedidos usará la IA con fallback local."
            : "API key eliminada. El análisis usará solo el buscador local.");
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (Application.Current == null) return;
        Application.Current.UserAppTheme = value ? AppTheme.Dark : AppTheme.Light;
        Preferences.Set("app_theme", value ? "dark" : "light");
    }
}
