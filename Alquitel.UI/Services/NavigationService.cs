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

        public NavigationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        private MainViewModel MainViewModel => _mainViewModel ??= _serviceProvider.GetRequiredService<MainViewModel>();

        public void NavigateTo<T>() where T : ObservableObject
        {
            var viewModel = _serviceProvider.GetRequiredService<T>();
            MainViewModel.CurrentViewModel = viewModel;
            if (viewModel is IAsyncInitialization asyncVm)
            {
                _ = asyncVm.InitializeAsync();
            }
        }

        public void NavigateTo<T>(T viewModel) where T : ObservableObject
        {
            MainViewModel.CurrentViewModel = viewModel;
            if (viewModel is IAsyncInitialization asyncVm)
            {
                _ = asyncVm.InitializeAsync();
            }
        }
    }
}
