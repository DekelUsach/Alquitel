using Alquitel.Mobile.ViewModels;

namespace Alquitel.Mobile.Views;

public partial class ClientsPage : ContentPage
{
    private readonly ClientsViewModel _vm;

    public ClientsPage(ClientsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }
}
