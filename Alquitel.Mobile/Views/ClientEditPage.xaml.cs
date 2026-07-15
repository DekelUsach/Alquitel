using Alquitel.Mobile.ViewModels;

namespace Alquitel.Mobile.Views;

public partial class ClientEditPage : ContentPage
{
    public ClientEditPage(ClientEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is ClientEditViewModel vm)
        {
            await vm.VerifyPermissionsAsync();
        }
    }
}
