namespace Alquitel.Core.Interfaces
{
    // Constraint "class" a propósito: la versión anterior exigía ObservableObject de
    // CommunityToolkit.Mvvm, lo que acoplaba la capa Core (dominio puro) a una librería
    // de patrón de presentación. La implementación WPF sigue trabajando con sus
    // ViewModels; el contrato no necesita conocerlos.
    public interface INavigationService
    {
        void NavigateTo<T>() where T : class;
        void NavigateTo<T>(T viewModel) where T : class;
    }
}
