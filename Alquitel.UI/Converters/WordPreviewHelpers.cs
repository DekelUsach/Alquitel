using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Alquitel.Core.Parsing;

namespace Alquitel.UI.Converters
{
    /// <summary>
    /// Convierte una ruta de archivo en un ImageSource cargado con BitmapCacheOption.OnLoad,
    /// para que la vista previa no deje el archivo de imagen bloqueado en disco.
    /// Devuelve null (imagen vacía) si la ruta no existe o no es una imagen válida.
    /// </summary>
    public class PathToImageSourceConverter : IValueConverter
    {
        public static readonly PathToImageSourceConverter Instance = new();

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Propiedad adjunta que construye los Inlines (Runs) de un TextBlock a partir de una
    /// lista de <see cref="TextSegment"/> de TagParser. Permite que la vista previa Word
    /// renderice texto con estilos mixtos y wrapping/justificado real (una WrapPanel de
    /// TextBlocks separados no envuelve palabra por palabra).
    /// </summary>
    public static class WordPreviewInlines
    {
        public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
            "Segments", typeof(IEnumerable<TextSegment>), typeof(WordPreviewInlines),
            new PropertyMetadata(null, OnSegmentsChanged));

        public static void SetSegments(DependencyObject element, IEnumerable<TextSegment>? value)
            => element.SetValue(SegmentsProperty, value);

        public static IEnumerable<TextSegment>? GetSegments(DependencyObject element)
            => (IEnumerable<TextSegment>?)element.GetValue(SegmentsProperty);

        private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock tb) return;
            tb.Inlines.Clear();
            if (e.NewValue is not IEnumerable<TextSegment> segments) return;

            foreach (var s in segments)
            {
                if (string.IsNullOrEmpty(s.Text)) continue;
                var run = new Run(s.Text)
                {
                    Foreground = BrushFromHex(s.ColorHex),
                    FontWeight = s.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = s.Italic ? FontStyles.Italic : FontStyles.Normal,
                };
                if (s.Underline) run.TextDecorations = TextDecorations.Underline;
                tb.Inlines.Add(run);
            }
        }

        private static Brush BrushFromHex(string? hex)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex ?? "#000000"));
                brush.Freeze();
                return brush;
            }
            catch { return Brushes.Black; }
        }
    }
}
