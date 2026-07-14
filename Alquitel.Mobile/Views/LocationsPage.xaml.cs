using Alquitel.Mobile.ViewModels;

namespace Alquitel.Mobile.Views;

public partial class LocationsPage : ContentPage
{
    private readonly LocationsViewModel _vm;

    public LocationsPage(LocationsViewModel vm)
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
