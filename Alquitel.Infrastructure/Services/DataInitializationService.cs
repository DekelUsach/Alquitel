using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Alquitel.Infrastructure.Services
{
    public class DataInitializationService
    {
        private readonly IDbContextFactory<AlquitelDbContext> _factory;

        public DataInitializationService(IDbContextFactory<AlquitelDbContext> factory)
        {
            _factory = factory;
        }

        public void Initialize()
        {
            using var context = _factory.CreateDbContext();

            if (context.Database.IsSqlite())
            {
                MigrateDatabase(context);
            }
            else
            {
                // Modo servidor (PostgreSQL/Supabase): el esquema se administra con las
                // migraciones SQL del proyecto Supabase, no con las migraciones EF locales.
                // Acá solo verificamos conectividad para fallar rápido con mensaje claro.
                if (!context.Database.CanConnect())
                    throw new InvalidOperationException(
                        "No se pudo conectar a la base de datos del servidor (Supabase). " +
                        "Verificá la conexión a internet y el ConnectionString en appsettings.json.");
            }

            SeedData(context);
            SeedUsers(context);
        }

        /// <summary>
        /// Applies EF Migrations. Handles legacy databases created by EnsureCreated() that have
        /// no __EFMigrationsHistory table — patches missing columns/indices via raw SQL, then
        /// registers the initial migration as already applied so future Migrate() calls work.
        /// </summary>
        private static void MigrateDatabase(AlquitelDbContext context)
        {
            bool hasMigrationHistory = HasTable(context, "__EFMigrationsHistory");

            if (hasMigrationHistory)
            {
                // Normal path: DB was already managed by migrations.
                context.Database.Migrate();
                return;
            }

            bool hasExistingTables = HasTable(context, "Products");

            if (!hasExistingTables)
            {
                // Brand-new DB: Migrate() creates everything from scratch.
                context.Database.Migrate();
                return;
            }

            // ── Legacy DB: tables exist but no migration tracking ────────
            AppLog.Information("Legacy database detected — applying schema upgrade in-place");

            // 1. Add missing columns (SQLite ALTER TABLE ADD COLUMN is idempotent-safe with IF check)
            AddColumnIfMissing(context, "Products", "IsArchived", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(context, "Clients", "IsArchived", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(context, "OrderItems", "DescriptionSnapshot", "TEXT");
            AddColumnIfMissing(context, "Orders", "EventEndDate", "TEXT");

            // 2. Create missing indices (IF NOT EXISTS is supported in SQLite)
            ExecuteSafe(context, "CREATE INDEX IF NOT EXISTS \"IX_Orders_CreatedDate\" ON \"Orders\" (\"CreatedDate\")");
            ExecuteSafe(context, "CREATE INDEX IF NOT EXISTS \"IX_Orders_ClientId\" ON \"Orders\" (\"ClientId\")");
            ExecuteSafe(context, "CREATE INDEX IF NOT EXISTS \"IX_OrderItems_OrderId\" ON \"OrderItems\" (\"OrderId\")");

            // 3. Create the __EFMigrationsHistory table and register the initial migration
            //    so future Migrate() calls don't try to re-create existing tables.
            ExecuteSafe(context, @"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" TEXT NOT NULL PRIMARY KEY,
                    ""ProductVersion"" TEXT NOT NULL
                )");

            // Register ONLY the initial migration as applied — the legacy patch above
            // replicates exactly what InitialCreate produces. Later migrations must
            // still run via Migrate() below, otherwise legacy DBs never receive them.
            var initialMigration = context.Database.GetPendingMigrations()
                .OrderBy(m => m, StringComparer.Ordinal)
                .FirstOrDefault(m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
            if (initialMigration != null)
            {
                ExecuteSafe(context, $@"
                    INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('{initialMigration}', '8.0.0')");
                AppLog.Information("Legacy database upgraded — registered {Migration} as applied", initialMigration);
            }

            // 4. Run Migrate() for any future migrations beyond what we just registered.
            context.Database.Migrate();
        }

        private static bool HasTable(AlquitelDbContext context, string tableName)
        {
            try
            {
                var conn = context.Database.GetDbConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
                var result = cmd.ExecuteScalar();
                return Convert.ToInt32(result) > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void AddColumnIfMissing(AlquitelDbContext context, string table, string column, string sqlType)
        {
            try
            {
                var conn = context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open) conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        return; // Column already exists
                }

                ExecuteSafe(context, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {sqlType}");
                AppLog.Information("Added column {Table}.{Column}", table, column);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "AddColumnIfMissing failed for {Table}.{Column}", table, column);
            }
        }

        private static void ExecuteSafe(AlquitelDbContext context, string sql)
        {
            try
            {
                context.Database.ExecuteSqlRaw(sql);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "ExecuteSafe failed: {Sql}", sql);
            }
        }

        // ── Seed data ────────────────────────────────────────────────
        private static void SeedData(AlquitelDbContext context)
        {
            // IgnoreQueryFilters: archived (soft-deleted) rows must count as existing,
            // otherwise archiving the seed data causes it to be re-inserted on every launch.
            if (!context.Products.IgnoreQueryFilters().Any())
            {
                context.Products.AddRange(new List<Product>
                {
                    new Product { Description = "Pantalla LED 2.6mm P2", Category = "Visuales", BasePrice = 1200 },
                    new Product { Description = "Touch Screen 85 Pro", Category = "Interactivos", BasePrice = 850 },
                    new Product { Description = "Notebook i9 Business Edition", Category = "Computación", BasePrice = 300 },
                    new Product { Description = "Logitech MeetUp 4K", Category = "Cámaras", BasePrice = 150 },
                    new Product { Description = "Servicio Técnico Plus (x Hora)", Category = "Servicios", BasePrice = 60 },
                    new Product { Description = "Traslado Moreno / La Rural", Category = "Logística", BasePrice = 450 }
                });
                context.SaveChanges();
            }

            const string DEMO_NAME = "DEMO - Pantalla de Leds 2 mm";
            if (!context.Products.IgnoreQueryFilters().Any(p => p.Description.StartsWith("DEMO -")))
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
                context.Products.Add(new Product
                {
                    Description = $"{DEMO_NAME} - [red]Para interior – [/red][green]FLEX[/green] [darkred][i]– Vertical[/i][/darkred]",
                    Category = "Visuales",
                    BasePrice = 1100000,
                    CustomFieldsJson = JsonSerializer.Serialize(demoFields)
                });
                context.SaveChanges();
            }

            SeedLocations(context);
        }

        /// <summary>
        /// Garantiza que siempre exista al menos un usuario Admin para poder entrar al
        /// sistema. Sin contraseña inicial: el Admin puede setearla desde Configuración.
        /// </summary>
        private static void SeedUsers(AlquitelDbContext context)
        {
            if (context.Users.Any()) return;

            context.Users.Add(new User
            {
                Name = "Admin",
                Role = UserRole.Admin,
                PasswordHash = null
            });
            context.SaveChanges();
            AppLog.Information("Usuario Admin inicial creado (sin contraseña)");
        }

        private static void SeedLocations(AlquitelDbContext context)
        {
            if (!context.Locations.Any())
            {
                context.Locations.AddRange(new List<Location>
                {
                    new Location { Name = "Moreno" },
                    new Location { Name = "La Rural" },
                    new Location { Name = "Costa Salguero" },
                    new Location { Name = "Centro Cultural Kirchner" }
                });
                context.SaveChanges();
            }
        }
    }
}
