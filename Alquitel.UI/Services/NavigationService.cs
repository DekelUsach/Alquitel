namespace Alquitel.UI.Services
{
    using Alquitel.Core.Interfaces;
    using CommunityToolkit.Mvvm.ComponentModel;
    using Microsoft.Extensions.DependencyInjection;
    using System;
    using Alquitel.UI.ViewModels;

    public class NavigationService : INavigationService
    {
        private readonly IServiceProvider _serviceProvider;
        private MainViewModel? _mainViewModel;

        // Mantiene la sidebar sincronizada cuando la navegación no parte de sus botones
        // (accesos rápidos del dashboard, atajos de teclado, "Repetir pedido", etc.).
        private static readonly System.Collections.Generic.Dictionary<Type, string> SectionByViewModel = new()
        {
            [typeof(DashboardViewModel)] = "Dashboard",
            [typeof(BudgetBuilderViewModel)] = "Presupuesto",
            [typeof(ProductEditorViewModel)] = "Productos",
            [typeof(ClientsViewModel)] = "Clientes",
            [typeof(LocationsViewModel)] = "Ubicaciones",
            [typeof(PresupuestosViewModel)] = "Presupuestos",
            [typeof(SettingsViewModel)] = "Configuración",
            [typeof(ReportsViewModel)] = "Reportes",
        };

        public NavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        private MainViewModel MainViewModel => _mainViewModel ??= _serviceProvider.GetRequiredService<MainViewModel>();

        public void NavigateTo<T>() where T : class
        {
            var viewModel = _serviceProvider.GetRequiredService<T>();
            SetCurrentViewModel(viewModel as ObservableObject
                ?? throw new InvalidOperationException($"{typeof(T).Name} no es un ViewModel (ObservableObject)."));
        }

        public void NavigateTo<T>(T viewModel) where T : class
        {
            SetCurrentViewModel(viewModel as ObservableObject
                ?? throw new InvalidOperationException($"{typeof(T).Name} no es un ViewModel (ObservableObject)."));
        }

        private void SetCurrentViewModel(ObservableObject viewModel)
        {
            var previous = MainViewModel.CurrentViewModel;
            MainViewModel.CurrentViewModel = viewModel;

            if (SectionByViewModel.TryGetValue(viewModel.GetType(), out var section))
                MainViewModel.ActiveSection = section;

            // Transient ViewModels own resources (FileSystemWatcher, autosave loops).
            // Without this, every navigation leaks the previous instance.
            if (!ReferenceEquals(previous, viewModel) && previous is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { Alquitel.Infrastructure.AppLog.Warning(ex, "Failed to dispose previous ViewModel {Vm}", previous.GetType().Name); }
            }

            if (viewModel is IAsyncInitialization asyncVm)
            {
                _ = InitializeSafeAsync(asyncVm);
            }
        }

        private static async System.Threading.Tasks.Task InitializeSafeAsync(IAsyncInitialization vm)
        {
            try { await vm.InitializeAsync(); }
            catch (Exception ex) { Alquitel.Infrastructure.AppLog.Error(ex, "ViewModel initialization failed for {Vm}", vm.GetType().Name); }
        }
    }
}
