using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Alquitel.Infrastructure.Persistence
{
    /// <summary>
    /// Design-time factory used exclusively by EF CLI tooling (dotnet ef migrations add/update).
    /// Not used at runtime — the real DbContext is wired through DI in App.xaml.cs.
    /// </summary>
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AlquitelDbContext>
    {
        public AlquitelDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AlquitelDbContext>();
            optionsBuilder.UseSqlite("Data Source=design_time.db");
            return new AlquitelDbContext(optionsBuilder.Options);
        }
    }
}
