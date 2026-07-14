using Alquitel.Mobile.Views;

namespace Alquitel.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        Routing.RegisterRoute("orderdetail", typeof(OrderDetailPage));
        Routing.RegisterRoute("clientedit", typeof(ClientEditPage));
        Routing.RegisterRoute("catalog", typeof(CatalogPage));
        Routing.RegisterRoute("productdetail", typeof(ProductDetailPage));
        Routing.RegisterRoute("locations", typeof(LocationsPage));
        Routing.RegisterRoute("reports", typeof(ReportsPage));
        Routing.RegisterRoute("settings", typeof(SettingsPage));
    }
}
