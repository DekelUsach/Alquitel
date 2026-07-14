using Alquitel.Mobile.ViewModels;

namespace Alquitel.Mobile.Views;

public partial class ClientEditPage : ContentPage
{
    public ClientEditPage(ClientEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
