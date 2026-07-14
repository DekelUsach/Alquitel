using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alquitel.Mobile.ViewModels;

public partial class MoreViewModel : BaseViewModel
{
    private readonly SessionService _session;

    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _roleName = string.Empty;

    public MoreViewModel(SessionService session) => _session = session;

    [RelayCommand]
    public Task InitializeAsync()
    {
        UserName = _session.UserName;
        RoleName = _session.CurrentUser?.Role.ToString() ?? string.Empty;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task GoCatalogAsync() => await Shell.Current.GoToAsync("catalog");

    [RelayCommand]
    private async Task GoLocationsAsync() => await Shell.Current.GoToAsync("locations");

    [RelayCommand]
    private async Task GoReportsAsync() => await Shell.Current.GoToAsync("reports");

    [RelayCommand]
    private async Task GoSettingsAsync() => await Shell.Current.GoToAsync("settings");

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (!await ConfirmAsync("Cerrar sesión", "¿Salir de la cuenta actual?")) return;
        _session.SignOut();
        await Shell.Current.GoToAsync("//login");
    }
}
