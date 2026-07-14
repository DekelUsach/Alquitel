namespace Alquitel.UI.Views
{
    public partial class BudgetBuilderView : System.Windows.Controls.UserControl
    {
        public BudgetBuilderView()
        {
            InitializeComponent();
        }

        // Order no implementa INotifyPropertyChanged, así que el VM no se entera cuando
        // el DatePicker escribe EventDate/EventEndDate. Este glue re-dispara la
        // validación de coherencia de fechas (fin < inicio) desde la vista.
        private void EventDatePicker_SelectedDateChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            (DataContext as ViewModels.BudgetBuilderViewModel)?.RefreshDateValidation();
        }

        // Location.Name tampoco notifica (Order/Location son POCOs): al tipear el lugar
        // se refresca el panel de avisos pre-generación ("Falta el lugar del evento").
        private void LocationTextBox_TextChanged(object sender,
            System.Windows.Controls.TextChangedEventArgs e)
        {
            (DataContext as ViewModels.BudgetBuilderViewModel)?.RefreshGenerationWarnings();
        }
    }
}
