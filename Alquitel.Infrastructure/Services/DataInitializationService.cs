using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Alquitel.Infrastructure.Services
{
    public class DataInitializationService
    {
        private readonly AlquitelDbContext _context;

        public DataInitializationService(AlquitelDbContext context)
        {
            _context = context;
        }

        public void Initialize()
        {
            _context.Database.EnsureCreated();

            if (!_context.Products.Any())
            {
                _context.Products.AddRange(new List<Product>
                {
                    new Product { Description = "Pantalla LED 2.6mm P2", Category = "Visuales", BasePrice = 1200 },
                    new Product { Description = "Touch Screen 85 Pro", Category = "Interactivos", BasePrice = 850 },
                    new Product { Description = "Notebook i9 Business Edition", Category = "Computación", BasePrice = 300 },
                    new Product { Description = "Logitech MeetUp 4K", Category = "Cámaras", BasePrice = 150 },
                    new Product { Description = "Servicio Técnico Plus (x Hora)", Category = "Servicios", BasePrice = 60 },
                    new Product { Description = "Traslado Moreno / La Rural", Category = "Logística", BasePrice = 450 }
                });
                _context.SaveChanges();
            }

            // Demo product showcasing the full Presupuesto rendering (inline color tags + custom fields)
            const string DEMO_NAME = "DEMO - Pantalla de Leds 2 mm";
            if (!_context.Products.Any(p => p.Description.StartsWith("DEMO -")))
            {
                var demoFields = new List<CustomFieldDefinition>
                {
                    new() { Label = "",                Value = "[green][b]Píxeles de 2.6 mm[/b][/green]",                ColorHex = "#000000" },
                    new() { Label = "",                Value = "Escalador/ controlador de leds",                          ColorHex = "#000000" },
                    new() { Label = "",                Value = "Módulos de 500 mm x 500 mm x 100 mm",                     ColorHex = "#000000" },
                    new() { Label = "Peso aprox.",     Value = "44 kg x m2.",          IsUnderline = true, ColorHex = "#000000" },
                    new() { Label = "Consumo aprox.",  Value = "4 amperes x m2",       IsUnderline = true, ColorHex = "#000000" },
                    new() { Label = "Resolución",      Value = "384 x 384 pixeles x m2", IsUnderline = true, ColorHex = "#000000" },
                    new() { Label = "",                Value = "1 rack de energía con disyuntor y térmica",               ColorHex = "#000000" },
                    new() { Label = "",                Value = "[b][i]Incluye estructura para montaje de piso tipo layher[/i][/b]", ColorHex = "#000000" }
                };
                _context.Products.Add(new Product
                {
                    Description = $"{DEMO_NAME} - [red]Para interior – [/red][green]FLEX[/green] [darkred][i]– Vertical[/i][/darkred]",
                    Category = "Visuales",
                    BasePrice = 1100000,
                    CustomFieldsJson = JsonSerializer.Serialize(demoFields)
                });
                _context.SaveChanges();
            }

            if (!_context.Locations.Any())
            {
                _context.Locations.AddRange(new List<Location>
                {
                    new Location { Name = "Moreno" },
                    new Location { Name = "La Rural" },
                    new Location { Name = "Costa Salguero" },
                    new Location { Name = "Centro Cultural Kirchner" }
                });
                _context.SaveChanges();
            }
        }
    }
}
