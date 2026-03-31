using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using System.Collections.Generic;
using System.Linq;

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
