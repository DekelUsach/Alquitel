namespace Alquitel.UI.Services
{
    using Alquitel.Core.Interfaces;
    using System.Windows;

    public class DialogService : IDialogService
    {
        public void ShowInfo(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void ShowWarning(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void ShowError(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public bool ShowConfirm(string title, string message)
        {
            var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }

        public string? ShowInput(string title, string hint, string initialValue = "")
        {
            var prompt = new Views.InputPromptWindow(title, hint, initialValue)
            {
                Owner = Application.Current.MainWindow
            };
            return prompt.ShowDialog() == true ? prompt.Value : null;
        }
    }
}
