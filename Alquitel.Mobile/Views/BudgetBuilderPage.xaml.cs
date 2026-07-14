using Alquitel.Mobile.ViewModels;

namespace Alquitel.Mobile.Views;

public partial class BudgetBuilderPage : ContentPage
{
    private readonly BudgetBuilderViewModel _vm;

    public BudgetBuilderPage(BudgetBuilderViewModel vm)
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
