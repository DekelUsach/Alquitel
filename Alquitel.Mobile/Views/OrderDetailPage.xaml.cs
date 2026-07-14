using Alquitel.Mobile.ViewModels;

namespace Alquitel.Mobile.Views;

public partial class OrderDetailPage : ContentPage
{
    public OrderDetailPage(OrderDetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
