using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alquitel.Mobile.ViewModels;

public partial class MoreViewModel : BaseViewModel
{
    private readonly SessionService _session;

    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _roleName = string.Empty;
    [ObservableProperty] private bool _canManageLocations;
    [ObservableProperty] private bool _canSeeReports;
    [ObservableProperty] private bool _isAdmin;

    public MoreViewModel(SessionService session) => _session = session;

    [RelayCommand]
    public Task InitializeAsync()
    {
        UserName = _session.UserName;
        RoleName = _session.CurrentUser?.Role.ToString() ?? string.Empty;
        CanManageLocations = _session.CanManageLocations;
        CanSeeReports = _session.CanSeeReports;
        IsAdmin = _session.IsAdmin;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task GoCatalogAsync() => await Shell.Current.GoToAsync("catalog");

    [RelayCommand]
    private async Task GoLocationsAsync()
    {
        if (!_session.CanManageLocations) return;
        await Shell.Current.GoToAsync("locations");
    }

    [RelayCommand]
    private async Task GoReportsAsync()
    {
        if (!_session.CanSeeReports) return;
        await Shell.Current.GoToAsync("reports");
    }

    [RelayCommand]
    private async Task GoSettingsAsync() => await Shell.Current.GoToAsync("settings");

    [RelayCommand]
    private async Task GoUserPermissionsAsync()
    {
        if (!_session.IsAdmin) return;
        await Shell.Current.GoToAsync("userpermissions");
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (!await ConfirmAsync("Cerrar sesión", "¿Salir de la cuenta actual?")) return;
        _session.SignOut();
        if (Shell.Current is AppShell appShell)
            appShell.UpdatePermissions();
        await Shell.Current.GoToAsync("//login");
    }
}
