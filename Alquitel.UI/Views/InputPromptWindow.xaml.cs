using System.Windows;

namespace Alquitel.UI.Views
{
    /// <summary>
    /// Prompt genérico de una línea (nombre de combo, etc.). Uso vía
    /// <see cref="Services.DialogService.ShowInput"/>; devuelve null al cancelar.
    /// </summary>
    public partial class InputPromptWindow : Window
    {
        public string? Value { get; private set; }

        public InputPromptWindow(string title, string hint, string initialValue = "")
        {
            InitializeComponent();
            Title = title;
            TitleText.Text = title;
            HintText.Text = hint;
            ValueInput.Text = initialValue;
            Loaded += (_, _) => { ValueInput.Focus(); ValueInput.SelectAll(); };
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            var text = ValueInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return; // sin valor no hay Aceptar
            Value = text;
            DialogResult = true;
        }
    }
}
