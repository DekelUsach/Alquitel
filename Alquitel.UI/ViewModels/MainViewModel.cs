using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Windows;

namespace Alquitel.UI.ViewModels
{
    /// <summary>
    /// Shell ViewModel — owns navigation state, theme toggle, and spawns child ViewModels.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDocumentService _documentService;
        private readonly INavigationService _navigationService;
        private readonly IAppSettings _appSettings;
        private readonly ICurrentUserService _currentUserService;
        private readonly IRemoteSyncService _remoteSyncService;

        [ObservableProperty]
        private ObservableObject? _currentViewModel;

        [ObservableProperty]
        private bool _isDarkMode;

        [ObservableProperty]
        private string _activeSection = "Dashboard";

        /// <summary>Versión del ensamblado mostrada en la barra de estado.</summary>
        public string AppVersion =>
            $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";

        // ── Multi-usuario / roles ────────────────────────────────────
        /// <summary>Gate de rol: los Vendedores no ven Productos, Reportes ni Configuración.</summary>
        public bool IsAdmin => _currentUserService.IsAdmin;

        /// <summary>Rol de depósito: solo ve las Órdenes de Trabajo.</summary>
        public bool IsArmador =>
            _currentUserService.Current?.Role == Alquitel.Core.Entities.UserRole.Armador;

        /// <summary>Gate de las secciones comerciales (todo menos OT). Falso para el Armador.</summary>
        public bool IsCommercial => !IsArmador;

        /// <summary>La sección de OT la ven el Admin y el Armador.</summary>
        public bool CanSeeWorkOrders => IsAdmin || IsArmador;

        public string CurrentUserName => _currentUserService.Current?.Name ?? string.Empty;

        public string CurrentUserRoleLabel => _currentUserService.Current?.Role switch
        {
            Alquitel.Core.Entities.UserRole.Admin => "Admin",
            Alquitel.Core.Entities.UserRole.Armador => "Armador",
            _ => "Vendedor"
        };

        /// <summary>Etiqueta de la barra de estado: base local o servidor compartido.</summary>
        public string DatabaseModeLabel => _remoteSyncService.IsRemoteConfigured
            ? (IsConnectionHealthy
                ? "Servidor compartido (Supabase)"
                : "Sin conexión al servidor — reintentando…")
            : "Base de datos local (SQLite)";

        /// <summary>
        /// Estado vivo de la conexión al servidor. Con SQLite local siempre true.
        /// Lo actualiza el chequeo periódico de <see cref="StartConnectionMonitor"/>.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DatabaseModeLabel))]
        private bool _isConnectionHealthy = true;

        /// <summary>
        /// Chequeo periódico de conectividad en modo servidor: si se cae internet la
        /// barra de estado lo muestra en claro en lugar de dejar que la app falle con
        /// timeouts crudos sin explicación.
        /// </summary>
        private void StartConnectionMonitor()
        {
            if (!_remoteSyncService.IsRemoteConfigured) return;

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var status = await _remoteSyncService.TestConnectionAsync();
                        IsConnectionHealthy = status.IsReachable;
                    }
                    catch
                    {
                        IsConnectionHealthy = false;
                    }
                    // Caído: reintento rápido para avisar la recuperación; sano: chequeo liviano por minuto.
                    await Task.Delay(TimeSpan.FromSeconds(IsConnectionHealthy ? 60 : 15));
                }
            });
        }

        /// <summary>Host de toasts: MainWindow bindea Toasts.Items para el overlay.</summary>
        public Alquitel.UI.Services.ToastService Toasts { get; }

        public MainViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDocumentService documentService, INavigationService navigationService, IAppSettings appSettings, ICurrentUserService currentUserService, IRemoteSyncService remoteSyncService, Alquitel.UI.Services.ToastService toastService)
        {
            _dbContextFactory = dbContextFactory;
            _documentService = documentService;
            _navigationService = navigationService;
            _appSettings = appSettings;
            _currentUserService = currentUserService;
            _remoteSyncService = remoteSyncService;
            Toasts = toastService;

            // Load theme preference from settings
            LoadThemePreference();
            ApplyTheme(IsDarkMode);

            // Navigate to Dashboard initially. We need to run it via dispatcher or just set it manually 
            // since NavigateTo<T> sets CurrentViewModel. Actually, since we are inside MainViewModel constructor,
            // we can just let it finish and maybe set the first view model from App.xaml.cs or just resolve it manually here.
            // But wait, NavigateTo<T> calls _serviceProvider.GetRequiredService<MainViewModel>() which is us!
        }

        public void Initialize()
        {
            // El Armador entra directo a su única pantalla: las Órdenes de Trabajo.
            if (IsArmador)
                NavigateToWorkOrders();
            else
                NavigateToDashboard();

            StartConnectionMonitor();
        }

        // ── Paleta de comandos (Ctrl+K) ──────────────────────────────

        [RelayCommand]
        private void OpenCommandPalette()
        {
            var palette = new Views.CommandPaletteWindow(this, _dbContextFactory)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            palette.ShowDialog();
        }

        /// <summary>Navega al armador con un VM ya cargado (presupuesto abierto desde la paleta).</summary>
        public void NavigateToLoadedBuilder(BudgetBuilderViewModel builder)
        {
            ActiveSection = "Presupuesto";
            _navigationService.NavigateTo(builder);
        }

        // ── Navigation Commands ──────────────────────────────────────

        [RelayCommand(CanExecute = nameof(IsCommercial))]
        private void NavigateToDashboard()
        {
            ActiveSection = "Dashboard";
            _navigationService.NavigateTo<DashboardViewModel>();
        }

        [RelayCommand(CanExecute = nameof(IsCommercial))]
        private void NavigateToBuilder()
        {
            ActiveSection = "Presupuesto";
            _navigationService.NavigateTo<BudgetBuilderViewModel>();
        }

        [RelayCommand(CanExecute = nameof(IsAdmin))]
        private void NavigateToSettings()
        {
            ActiveSection = "Configuración";
            _navigationService.NavigateTo<SettingsViewModel>();
        }

        [RelayCommand(CanExecute = nameof(IsAdmin))]
        private void NavigateToProducts()
        {
            ActiveSection = "Productos";
            _navigationService.NavigateTo<ProductEditorViewModel>();
        }

        [RelayCommand(CanExecute = nameof(IsAdmin))]
        private void NavigateToReports()
        {
            ActiveSection = "Reportes";
            _navigationService.NavigateTo<ReportsViewModel>();
        }

        [RelayCommand(CanExecute = nameof(IsCommercial))]
        private void NavigateToPresupuestos()
        {
            ActiveSection = "Presupuestos";
            _navigationService.NavigateTo<PresupuestosViewModel>();
        }

        [RelayCommand(CanExecute = nameof(IsCommercial))]
        private void NavigateToOrderPool()
        {
            ActiveSection = "Seguimiento";
            _navigationService.NavigateTo<OrderPoolViewModel>();
        }

        [RelayCommand(CanExecute = nameof(CanSeeWorkOrders))]
        private void NavigateToWorkOrders()
        {
            ActiveSection = "OrdenesTrabajo";
            _navigationService.NavigateTo<WorkOrdersViewModel>();
        }

        [RelayCommand(CanExecute = nameof(IsCommercial))]
        private void NavigateToClients()
        {
            ActiveSection = "Clientes";
            _navigationService.NavigateTo<ClientsViewModel>();
        }

        [RelayCommand(CanExecute = nameof(IsCommercial))]
        private void NavigateToLocations()
        {
            ActiveSection = "Ubicaciones";
            _navigationService.NavigateTo<LocationsViewModel>();
        }

        // ── Theme ────────────────────────────────────────────────────

        [RelayCommand]
        private void ToggleTheme()
        {
            IsDarkMode = !IsDarkMode;
            ApplyTheme(IsDarkMode);
            SaveThemePreferenceSilent();
        }

        private void ApplyTheme(bool isDark)
        {
            var themeFile = isDark ? "DarkTheme.xaml" : "LightTheme.xaml";
            var uri = new Uri($"pack://application:,,,/Themes/{themeFile}");
            var mergedDicts = Application.Current.Resources.MergedDictionaries;

            var toRemove = new List<ResourceDictionary>();
            foreach (var d in mergedDicts)
            {
                if (d.Source?.OriginalString.Contains("Theme.xaml") == true)
                    toRemove.Add(d);
            }
            foreach (var d in toRemove) mergedDicts.Remove(d);

            mergedDicts.Add(new ResourceDictionary { Source = uri });
        }

        private void LoadThemePreference()
        {
            IsDarkMode = _appSettings.IsDarkMode;
        }

        /// <summary>
        /// Persists the theme preference without showing any MessageBox.
        /// This fixes the bug where toggling dark mode showed "Rutas guardadas correctamente."
        /// </summary>
        private void SaveThemePreferenceSilent()
        {
            _appSettings.IsDarkMode = IsDarkMode;
            _appSettings.SaveSettings();
        }
    }
}
