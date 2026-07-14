using Alquitel.Mobile.Services;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.Data;

/// <summary>
/// Fábrica de contextos que lee la ConnectionString vigente en cada creación,
/// para que un cambio desde Ajustes aplique sin reiniciar la app.
/// </summary>
public class MobileDbContextFactory : IDbContextFactory<MobileDbContext>
{
    public MobileDbContext CreateDbContext()
    {
        if (!AppConfig.IsDbConfigured)
            throw new InvalidOperationException("La conexión a la base de datos no está configurada. Cargala desde Ajustes.");

        var options = new DbContextOptionsBuilder<MobileDbContext>()
            .UseNpgsql(AppConfig.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        return new MobileDbContext(options);
    }
}
