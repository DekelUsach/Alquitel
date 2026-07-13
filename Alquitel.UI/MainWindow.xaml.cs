using System;
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
            ClampToWorkArea();
        }

        /// <summary>
        /// Micro-animación al navegar entre secciones: fade + slide sutil de 200 ms.
        /// Solo transform/opacity (sin layout) y se omite si el usuario deshabilitó
        /// las animaciones del sistema (accesibilidad / reduced motion).
        /// </summary>
        private void MainContent_TargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
        {
            if (!SystemParameters.ClientAreaAnimation) return;

            var duration = TimeSpan.FromMilliseconds(200);
            var ease = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            };

            MainContent.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease });

            if (MainContent.RenderTransform is System.Windows.Media.TranslateTransform tt)
            {
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(10, 0, duration) { EasingFunction = ease });
            }
        }

        /// <summary>
        /// Ajusta el tamaño inicial al área de trabajo del monitor (excluye la barra
        /// de tareas). Sin esto, en pantallas chicas o con escalado DPI alto la ventana
        /// de 1440x900 queda más grande que la pantalla: la barra de título (y el botón
        /// de minimizar) quedan fuera del área visible y partes de la UI no se pueden tocar.
        /// </summary>
        private void ClampToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;

            if (Width > workArea.Width)
                Width = Math.Max(MinWidth, workArea.Width * 0.97);
            if (Height > workArea.Height)
                Height = Math.Max(MinHeight, workArea.Height * 0.94);

            // Centrado manual dentro del área de trabajo: CenterScreen usa la pantalla
            // completa y puede empujar la barra de título por encima del borde superior.
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }
    }
}
