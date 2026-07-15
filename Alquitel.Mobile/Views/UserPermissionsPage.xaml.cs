using Alquitel.Mobile.ViewModels;

namespace Alquitel.Mobile.Views;

public partial class UserPermissionsPage : ContentPage
{
    private readonly UserPermissionsViewModel _vm;

    public UserPermissionsPage(UserPermissionsViewModel vm)
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
