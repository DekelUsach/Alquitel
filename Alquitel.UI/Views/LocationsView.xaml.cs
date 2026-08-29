using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Alquitel.UI.Views
{
    public partial class LocationsView : UserControl
    {
        public LocationsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Único code-behind de la vista: llevar el foco al campo de nombre cuando se
        /// abre la ficha lateral. Es una preocupación de vista pura (no hay forma
        /// MVVM-pura de enfocar un control) y todo lo demás pasa por comandos.
        /// En particular: acá NO se guarda nada al perder el foco — un clic al costado
        /// renombraría un lugar cuyo nombre se imprime en presupuestos futuros.
        /// </summary>
        private void EditNameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true && sender is TextBox tb)
            {
                // BeginInvoke en prioridad Input: al momento del evento la ficha todavía
                // está entrando y un Focus() directo se pierde.
                tb.Dispatcher.BeginInvoke(new Action(() =>
                {
                    tb.Focus();
                    tb.SelectAll();
                }), DispatcherPriority.Input);
            }
        }
    }
}
