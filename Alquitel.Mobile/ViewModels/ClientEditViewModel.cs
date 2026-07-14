using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Mobile.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.ViewModels;

[QueryProperty(nameof(ClientId), "clientId")]
public partial class ClientEditViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;

    [ObservableProperty] private Guid _clientId;
    [ObservableProperty] private string _pageTitle = "Nuevo cliente";
    [ObservableProperty] private string _companyName = string.Empty;
    [ObservableProperty] private string _cuit = string.Empty;
    [ObservableProperty] private string? _cuitFeedback;
    [ObservableProperty] private bool _cuitValid = true;
    [ObservableProperty] private string? _contactName;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _phone;
    [ObservableProperty] private string? _internalNotes;
    [ObservableProperty] private string _specialDiscountText = string.Empty;
    [ObservableProperty] private bool _isExisting;

    public ClientEditViewModel(IDbContextFactory<MobileDbContext> factory) => _factory = factory;

    partial void OnClientIdChanged(Guid value) => _ = LoadAsync();

    partial void OnCuitChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            CuitFeedback = null;
            CuitValid = true;
            return;
        }
        CuitValid = CuitValidator.IsValid(value);
        CuitFeedback = CuitValid ? "✓ CUIT válido" : "CUIT inválido (dígito verificador incorrecto)";
    }

    private async Task LoadAsync()
    {
        if (ClientId == Guid.Empty) return;
        try
        {
            using var db = _factory.CreateDbContext();
            var client = await db.Clients.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == ClientId);
            if (client == null) return;

            IsExisting = true;
            PageTitle = "Editar cliente";
            CompanyName = client.CompanyName;
            Cuit = client.Cuit;
            ContactName = client.ContactName;
            Email = client.Email;
            Phone = client.Phone;
            InternalNotes = client.InternalNotes;
            SpecialDiscountText = client.SpecialDiscountPercent?.ToString("0.##") ?? string.Empty;
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(CompanyName))
        {
            await ShowAlertAsync("Cliente", "Ingresá la razón social.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(Cuit) && !CuitValid)
        {
            await ShowAlertAsync("Cliente", "El CUIT no es válido.");
            return;
        }
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            using var db = _factory.CreateDbContext();

            Client client;
            if (ClientId != Guid.Empty)
            {
                client = await db.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == ClientId);
            }
            else
            {
                client = new Client();
                db.Clients.Add(client);
            }

            client.CompanyName = CompanyName.Trim();
            client.Cuit = Cuit.Trim();
            client.ContactName = string.IsNullOrWhiteSpace(ContactName) ? null : ContactName.Trim();
            client.Email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
            client.Phone = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim();
            client.InternalNotes = string.IsNullOrWhiteSpace(InternalNotes) ? null : InternalNotes.Trim();
            client.SpecialDiscountPercent =
                decimal.TryParse(SpecialDiscountText.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct) && pct > 0
                    ? Math.Clamp(pct, 0m, 100m) : null;

            await db.SaveChangesAsync();
            await Shell.Current.GoToAsync("..");
        }
        catch (DbUpdateException)
        {
            await ShowAlertAsync("Cliente", "Ya existe un cliente con ese CUIT.");
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Error", DescribeDbError(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ArchiveAsync()
    {
        if (ClientId == Guid.Empty) return;
        if (!await ConfirmAsync("Archivar cliente", "El cliente se oculta de las listas pero se conserva en el historial. ¿Continuar?"))
            return;

        try
        {
            IsBusy = true;
            using var db = _factory.CreateDbContext();
            var client = await db.Clients.IgnoreQueryFilters().FirstAsync(c => c.Id == ClientId);
            client.IsArchived = true;
            await db.SaveChangesAsync();
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Error", DescribeDbError(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
