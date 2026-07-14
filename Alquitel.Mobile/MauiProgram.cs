using Alquitel.Core.Interfaces;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Services;
using Alquitel.Mobile.ViewModels;
using Alquitel.Mobile.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Alquitel.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var services = builder.Services;

        // ── Datos ──
        services.AddSingleton<IDbContextFactory<MobileDbContext>, MobileDbContextFactory>();

        // ── Servicios ──
        services.AddSingleton<SessionService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<OrderService>();
        services.AddSingleton<ApprovalService>();
        services.AddSingleton<IAiOrderParser, MobileAiOrderParser>();

        // ── ViewModels ──
        services.AddTransient<LoginViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddSingleton<BudgetBuilderViewModel>(); // el carrito sobrevive a la navegación
        services.AddTransient<OrdersViewModel>();
        services.AddTransient<OrderDetailViewModel>();
        services.AddTransient<ClientsViewModel>();
        services.AddTransient<ClientEditViewModel>();
        services.AddTransient<CatalogViewModel>();
        services.AddTransient<ProductDetailViewModel>();
        services.AddTransient<LocationsViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MoreViewModel>();

        // ── Views ──
        services.AddTransient<LoginPage>();
        services.AddTransient<DashboardPage>();
        services.AddTransient<BudgetBuilderPage>();
        services.AddTransient<OrdersPage>();
        services.AddTransient<OrderDetailPage>();
        services.AddTransient<ClientsPage>();
        services.AddTransient<ClientEditPage>();
        services.AddTransient<CatalogPage>();
        services.AddTransient<ProductDetailPage>();
        services.AddTransient<LocationsPage>();
        services.AddTransient<ReportsPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<MorePage>();

        return builder.Build();
    }
}
