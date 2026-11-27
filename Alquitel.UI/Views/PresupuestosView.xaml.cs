using Alquitel.UI.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Alquitel.UI.Views
{
    public partial class PresupuestosView : UserControl
    {
        public PresupuestosView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is PresupuestosViewModel oldVm)
                oldVm.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is PresupuestosViewModel newVm)
                newVm.PropertyChanged += OnVmPropertyChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is PresupuestosViewModel vm)
            {
                vm.PropertyChanged -= OnVmPropertyChanged;
                vm.Dispose();
            }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PresupuestosViewModel.IsDetailPanelOpen)) return;
            var vm = (PresupuestosViewModel)DataContext;
            AnimatePanel(vm.IsDetailPanelOpen);
        }

        private void AnimatePanel(bool open)
        {
            var transform = (TranslateTransform)DetailPanel.RenderTransform;
            var width = DetailPanel.ActualWidth > 0 ? DetailPanel.ActualWidth : DetailPanel.Width;
            // Abrir: desacelera largo y asienta. Cerrar: acelera corto y se va (la salida no se contempla).
            var animation = new DoubleAnimation
            {
                To = open ? 0 : width,
                Duration = !SystemParameters.ClientAreaAnimation ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(open ? 340 : 180),
                EasingFunction = open
                    ? new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 }
                    : new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void DimOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (DataContext is PresupuestosViewModel vm)
                vm.CloseDetailCommand.Execute(null);
        }

        private void FilesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (DataContext is PresupuestosViewModel vm && vm.SelectedFile != null)
                vm.OpenFileOnDisk(vm.SelectedFile.FullPath);
        }
    }
}
