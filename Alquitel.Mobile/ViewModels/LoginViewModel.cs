using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alquitel.Mobile.ViewModels;

public partial class LoginViewModel : BaseViewModel
{
    private readonly AuthService _auth;
    private readonly SessionService _session;

    public ObservableCollection<User> Users { get; } = new();

    [ObservableProperty]
    private User? _selectedUser;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _needsConfiguration;

    [ObservableProperty]
    private string _connectionString = string.Empty;

    [ObservableProperty]
    private bool _usersLoaded;

    public LoginViewModel(AuthService auth, SessionService session)
    {
        _auth = auth;
        _session = session;
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        await AppConfig.InitializeAsync();
        NeedsConfiguration = !AppConfig.IsDbConfigured;
        if (!NeedsConfiguration)
            await LoadUsersAsync();
    }

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            await ShowAlertAsync("Configuración", "Pegá la cadena de conexión de Supabase (pooler).");
            return;
        }
        await AppConfig.SaveConnectionStringAsync(ConnectionString);
        NeedsConfiguration = false;
        await LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            var users = await _auth.GetUsersAsync();
            Users.Clear();
            foreach (var u in users) Users.Add(u);

            var remembered = Preferences.Get("last_user", string.Empty);
            SelectedUser = Users.FirstOrDefault(u => u.Name == remembered) ?? Users.FirstOrDefault();
            UsersLoaded = Users.Count > 0;
            if (!UsersLoaded)
                ErrorMessage = "No hay usuarios cargados en el sistema. Crealos desde la app de escritorio.";
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
            NeedsConfiguration = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (SelectedUser == null)
        {
            await ShowAlertAsync("Ingreso", "Elegí tu usuario.");
            return;
        }
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            var user = await _auth.LoginAsync(SelectedUser.Id, Password);
            if (user == null)
            {
                ErrorMessage = "Contraseña incorrecta.";
                return;
            }

            var permissions = await _auth.GetPermissionsAsync(user.Id);
            _session.SignIn(user, permissions);
            if (Shell.Current is AppShell appShell)
                appShell.UpdatePermissions();
            Preferences.Set("last_user", user.Name);
            Password = string.Empty;
            await Shell.Current.GoToAsync("//main/dashboard");
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
