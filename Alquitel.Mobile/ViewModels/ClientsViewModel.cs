using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

using Alquitel.Mobile.Services;

namespace Alquitel.Mobile.ViewModels;

public partial class ClientsViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly SessionService _session;
    private List<Client> _all = new();

    public ObservableCollection<Client> Clients { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;

    public ClientsViewModel(IDbContextFactory<MobileDbContext> factory, SessionService session)
    {
        _factory = factory;
        _session = session;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_session.CanManageClients)
        {
            await ShowAlertAsync("Acceso denegado", "No tienes permisos para ver clientes.");
            await Shell.Current.GoToAsync("//main/dashboard");
            return;
        }
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            using var db = _factory.CreateDbContext();
            _all = await db.Clients.AsNoTracking().OrderBy(c => c.CompanyName).ToListAsync();
            ApplyFilter();
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

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = _all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            query = query.Where(c =>
                c.CompanyName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                c.Cuit.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                (c.ContactName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        Clients.Clear();
        foreach (var c in query) Clients.Add(c);
    }

    [RelayCommand]
    private async Task EditClientAsync(Client? client)
    {
        if (!_session.CanManageClients) return;
        var args = new Dictionary<string, object>();
        if (client != null) args["clientId"] = client.Id;
        await Shell.Current.GoToAsync("clientedit", args);
    }

    [RelayCommand]
    private async Task NewClientAsync()
    {
        if (!_session.CanManageClients) return;
        await Shell.Current.GoToAsync("clientedit");
    }
}
