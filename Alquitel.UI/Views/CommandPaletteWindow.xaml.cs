using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Alquitel.Infrastructure.Persistence;
using Alquitel.UI.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.UI.Views
{
    /// <summary>Resultado de la paleta: etiqueta + acción a ejecutar al elegirlo.</summary>
    public sealed record PaletteItem(string Icon, string Label, string Category, Action Execute);

    /// <summary>
    /// Paleta de comandos global (Ctrl+K): acciones de navegación + búsqueda de
    /// clientes, productos y presupuestos en un solo lugar (patrón VS Code/Linear).
    /// </summary>
    public partial class CommandPaletteWindow : Window
    {
        private readonly MainViewModel _main;
        private readonly IDbContextFactory<AlquitelDbContext> _dbFactory;
        private readonly List<PaletteItem> _staticItems;
        private readonly ObservableCollection<PaletteItem> _results = new();
        private int _searchVersion;

        public CommandPaletteWindow(MainViewModel main, IDbContextFactory<AlquitelDbContext> dbFactory)
        {
            _main = main;
            _dbFactory = dbFactory;
            InitializeComponent();
            ResultsList.ItemsSource = _results;

            _staticItems = BuildStaticItems();
            RefreshResults(string.Empty);
            Loaded += (_, _) => SearchBox.Focus();
        }

        private List<PaletteItem> BuildStaticItems()
        {
            var items = new List<PaletteItem>();
            void Nav(string icon, string label, System.Windows.Input.ICommand command)
            {
                if (command.CanExecute(null))
                    items.Add(new PaletteItem(icon, label, "IR A", () => command.Execute(null)));
            }

            Nav("", "Dashboard", _main.NavigateToDashboardCommand);
            Nav("", "Crear Presupuesto", _main.NavigateToBuilderCommand);
            Nav("", "Presupuestos", _main.NavigateToPresupuestosCommand);
            Nav("", "Seguimiento", _main.NavigateToOrderPoolCommand);
            Nav("", "Reportes", _main.NavigateToReportsCommand);
            Nav("", "Órdenes de Trabajo", _main.NavigateToWorkOrdersCommand);
            Nav("", "Productos", _main.NavigateToProductsCommand);
            Nav("", "Clientes", _main.NavigateToClientsCommand);
            Nav("", "Ubicaciones (nueva ubicación)", _main.NavigateToLocationsCommand);
            Nav("", "Configuración", _main.NavigateToSettingsCommand);
            items.Add(new PaletteItem("", "Cambiar tema claro/oscuro", "ACCIÓN",
                () => _main.ToggleThemeCommand.Execute(null)));
            return items;
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => RefreshResults(SearchBox.Text);

        private void RefreshResults(string query)
        {
            query = (query ?? string.Empty).Trim();
            _results.Clear();

            foreach (var item in _staticItems.Where(i =>
                         query.Length == 0 ||
                         i.Label.Contains(query, StringComparison.OrdinalIgnoreCase)))
                _results.Add(item);

            if (ResultsList.Items.Count > 0) ResultsList.SelectedIndex = 0;

            // Búsqueda en la base solo con 2+ caracteres (evita traer todo el catálogo).
            if (query.Length >= 2)
                _ = SearchDatabaseAsync(query, ++_searchVersion);
        }

        private async Task SearchDatabaseAsync(string query, int version)
        {
            try
            {
                using var db = await _dbFactory.CreateDbContextAsync();

                var clients = await db.Clients.AsNoTracking()
                    .Where(c => EF.Functions.Like(c.CompanyName, $"%{query}%"))
                    .OrderBy(c => c.CompanyName).Take(5).ToListAsync();

                var products = await db.Products.AsNoTracking()
                    .Where(p => EF.Functions.Like(p.Description, $"%{query}%"))
                    .OrderBy(p => p.Description).Take(5).ToListAsync();

                var orders = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                    .Where(o => EF.Functions.Like(o.BudgetNumber, $"%{query}%"))
                    .OrderByDescending(o => o.CreatedDate).Take(5)
                    .Select(o => new { o.Id, o.BudgetNumber })
                    .ToListAsync();

                // Otro tipeo ya disparó una búsqueda más nueva: descartar esta.
                if (version != _searchVersion) return;

                foreach (var c in clients)
                    _results.Add(new PaletteItem("", c.CompanyName, "CLIENTE",
                        () => _main.NavigateToClientsCommand.Execute(null)));

                foreach (var p in products)
                    _results.Add(new PaletteItem("",
                        Alquitel.Core.Parsing.TagParser.StripTags(p.Description) ?? p.Description,
                        "PRODUCTO",
                        () => _main.NavigateToProductsCommand.Execute(null)));

                foreach (var o in orders)
                {
                    var orderId = o.Id;
                    _results.Add(new PaletteItem("", $"Presupuesto {o.BudgetNumber}", "ABRIR",
                        () => _ = OpenOrderInBuilderAsync(orderId)));
                }

                if (ResultsList.SelectedIndex < 0 && _results.Count > 0)
                    ResultsList.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Alquitel.Infrastructure.AppLog.Warning(ex, "Command palette search failed");
            }
        }

        private async Task OpenOrderInBuilderAsync(Guid orderId)
        {
            var sp = App.ServiceProvider;
            if (sp == null) return;
            var builder = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<BudgetBuilderViewModel>(sp);
            if (await builder.LoadOrderForEditAsync(orderId))
                _main.NavigateToLoadedBuilder(builder);
        }

        // Close() dispara Deactivated (el foco vuelve al Owner), y Deactivated volvía a
        // llamar Close() sobre una ventana ya en cierre → InvalidOperationException
        // ("no se puede llamar a Show/ShowDialog/Close mientras se está cerrando").
        // Todo cierre pasa por acá y es idempotente.
        private bool _isClosing;

        private void SafeClose()
        {
            if (_isClosing) return;
            _isClosing = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            base.OnClosing(e);
        }

        private void ExecuteSelected()
        {
            if (ResultsList.SelectedItem is not PaletteItem item) return;
            SafeClose();
            item.Execute();
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelected();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    SafeClose();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    ExecuteSelected();
                    e.Handled = true;
                    break;
                case Key.Down when SearchBox.IsKeyboardFocused:
                    if (ResultsList.Items.Count > 0)
                    {
                        ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
                        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                    }
                    e.Handled = true;
                    break;
                case Key.Up when SearchBox.IsKeyboardFocused:
                    if (ResultsList.Items.Count > 0)
                    {
                        ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0);
                        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                    }
                    e.Handled = true;
                    break;
            }
        }

        // La paleta es efímera: perder el foco la cierra (mismo patrón que VS Code).
        private void Window_Deactivated(object? sender, EventArgs e) => SafeClose();
    }
}
