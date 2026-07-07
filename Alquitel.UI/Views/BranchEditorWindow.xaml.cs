using System.Linq;
using System.Windows;
using Alquitel.UI.ViewModels;

namespace Alquitel.UI.Views
{
    /// <summary>
    /// Editor de ramificación de presupuestos: diálogo modal que muestra la versión
    /// nueva ("31294 → 31294/2") con ajuste rápido de items antes de crear la rama.
    /// Devuelve DialogResult=true cuando el usuario confirma.
    /// </summary>
    public partial class BranchEditorWindow : Window
    {
        public BranchEditorViewModel ViewModel { get; }

        public BranchEditorWindow(BranchEditorViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.Items.Any(i => i.Include))
            {
                MessageBox.Show(this, "La nueva versión necesita al menos un ítem incluido.",
                    "Nueva versión", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
