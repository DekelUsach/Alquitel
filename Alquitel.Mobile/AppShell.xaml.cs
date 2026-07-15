using Alquitel.Mobile.Services;
using Alquitel.Mobile.Views;

namespace Alquitel.Mobile;

public partial class AppShell : Shell
{
    private readonly SessionService _session;

    public AppShell(SessionService session)
    {
        _session = session;
        InitializeComponent();

        Routing.RegisterRoute("orderdetail", typeof(OrderDetailPage));
        Routing.RegisterRoute("clientedit", typeof(ClientEditPage));
        Routing.RegisterRoute("catalog", typeof(CatalogPage));
        Routing.RegisterRoute("productdetail", typeof(ProductDetailPage));
        Routing.RegisterRoute("locations", typeof(LocationsPage));
        Routing.RegisterRoute("reports", typeof(ReportsPage));
        Routing.RegisterRoute("settings", typeof(SettingsPage));
        Routing.RegisterRoute("userpermissions", typeof(UserPermissionsPage));
        Routing.RegisterRoute("userpermissionedit", typeof(UserPermissionEditPage));
    }

    public void UpdatePermissions()
    {
        var role = _session.CurrentUser?.Role;
        bool isCommercial = role is Alquitel.Core.Entities.UserRole.Admin or Alquitel.Core.Entities.UserRole.Vendedor;

        BudgetTab.IsVisible = isCommercial;
        ClientsTab.IsVisible = isCommercial;
    }
}
