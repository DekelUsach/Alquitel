using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;

namespace Alquitel.UI.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly Action _navigateToBuilder;

        [ObservableProperty]
        private int _totalProducts;

        [ObservableProperty]
        private int _totalClients;

        [ObservableProperty]
        private int _totalOrders;

        [ObservableProperty]
        private string _welcomeMessage = string.Empty;

        public DashboardViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, Action navigateToBuilder)
        {
            _dbContextFactory = dbContextFactory;
            _navigateToBuilder = navigateToBuilder;
            LoadMetrics();
        }

        private void LoadMetrics()
        {
            try
            {
                using var db = _dbContextFactory.CreateDbContext();
                TotalProducts = db.Products.Count();
                TotalClients = db.Clients.Count();
                TotalOrders = db.Orders.Count();

                var hour = DateTime.Now.Hour;
                WelcomeMessage = hour switch
                {
                    < 12 => "Buenos días",
                    < 18 => "Buenas tardes",
                    _ => "Buenas noches"
                };
            }
            catch (Exception ex)
            {
                Alquitel.Infrastructure.AppLog.Warning(ex, "Dashboard metrics load failed");
                TotalProducts = 0;
                TotalClients = 0;
                TotalOrders = 0;
                WelcomeMessage = "Bienvenido";
            }
        }

        [RelayCommand]
        private void NewBudget() => _navigateToBuilder();

        [RelayCommand]
        private void Refresh() => LoadMetrics();
    }
}
