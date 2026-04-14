using System.Windows;
using Alquitel.UI.ViewModels;

namespace Alquitel.UI
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            DataContext = viewModel;
            InitializeComponent();
        }
    }
}
