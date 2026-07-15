using Alquitel.Mobile.ViewModels;

namespace Alquitel.Mobile.Views;

public partial class UserPermissionEditPage : ContentPage
{
    public UserPermissionEditPage(UserPermissionEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
