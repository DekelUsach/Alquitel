using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.ViewModels;

public partial class UserPermissionsViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly SessionService _session;

    public ObservableCollection<User> Users { get; } = new();

    [ObservableProperty] private bool _isRefreshing;

    public UserPermissionsViewModel(IDbContextFactory<MobileDbContext> factory, SessionService session)
    {
        _factory = factory;
        _session = session;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_session.IsAdmin)
        {
            await ShowAlertAsync("Acceso denegado", "Solo los administradores pueden gestionar permisos.");
            await Shell.Current.GoToAsync("//main/dashboard");
            return;
        }
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            using var db = _factory.CreateDbContext();
            var nonAdmins = await db.Users
                .Where(u => u.Role != UserRole.Admin && !u.IsArchived)
                .OrderBy(u => u.Name)
                .AsNoTracking()
                .ToListAsync();

            Users.Clear();
            foreach (var user in nonAdmins)
            {
                Users.Add(user);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
        }
        finally
        {
            IsBusy = false;
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task EditPermissionsAsync(User? user)
    {
        if (user == null) return;
        await Shell.Current.GoToAsync($"userpermissionedit", new Dictionary<string, object>
        {
            ["userId"] = user.Id
        });
    }
}
