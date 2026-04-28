namespace Alquitel.Core.Interfaces
{
    using CommunityToolkit.Mvvm.ComponentModel;

    public interface INavigationService
    {
        void NavigateTo<T>() where T : ObservableObject;
        void NavigateTo<T>(T viewModel) where T : ObservableObject;
    }
}
