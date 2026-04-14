using System.Windows;

namespace Alquitel.UI.Helpers
{
    /// <summary>
    /// Freezable proxy para bindings en contextos sin DataContext (e.g. DataGridColumn.Visibility).
    /// </summary>
    public class BindingProxy : Freezable
    {
        protected override Freezable CreateInstanceCore() => new BindingProxy();

        public object Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy),
                new FrameworkPropertyMetadata(null));
    }
}
