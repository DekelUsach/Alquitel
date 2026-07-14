using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación de <see cref="IRemoteSyncService"/> para el modo servidor
    /// (Database:Provider = "supabase"/"postgres"). El DbContext inyectado ya apunta a
    /// PostgreSQL, así que TestConnectionAsync es una prueba real de conectividad.
    /// PushPendingChangesAsync hace la carga inicial one-shot: lee la base SQLite local
    /// histórica de esta máquina y upserta todo en el servidor conservando los Guid.
    /// </summary>
    public class PostgresSyncService : IRemoteSyncService
    {
        private readonly IDbContextFactory<AlquitelDbContext> _remoteFactory;

        public PostgresSyncService(IDbContextFactory<AlquitelDbContext> remoteFactory)
        {
            _remoteFactory = remoteFactory;
        }

        public bool IsRemoteConfigured => true;

        public async Task<RemoteConnectionStatus> TestConnectionAsync()
        {
            try
            {
                using var db = await _remoteFactory.CreateDbContextAsync();
                var reachable = await db.Database.CanConnectAsync();
                return new RemoteConnectionStatus(
                    IsConfigured: true,
                    IsReachable: reachable,
                    Message: reachable
                        ? "Conectado al servidor PostgreSQL (Supabase)."
                        : "El servidor está configurado pero no responde. Revisá la conexión a internet.");
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "TestConnectionAsync failed");
                return new RemoteConnectionStatus(true, false, $"Error de conexión: {ex.Message}");
            }
        }

        public async Task PushPendingChangesAsync()
        {
            if (!File.Exists(AppPaths.DbFilePath))
            {
                AppLog.Information("PushPendingChangesAsync: no existe base SQLite local, nada que subir");
                return;
            }

            var localOptions = new DbContextOptionsBuilder<AlquitelDbContext>()
                .UseSqlite(AppPaths.DbConnectionString)
                .Options;

            using var local = new AlquitelDbContext(localOptions);
            using var remote = await _remoteFactory.CreateDbContextAsync();

            // Transacción única: una carga inicial que falla a mitad no debe dejar el
            // servidor en estado parcial (clientes subidos sin sus órdenes, etc.).
            await using var tx = await remote.Database.BeginTransactionAsync();

            // Orden por dependencias de FK: primero las tablas referenciadas.
            var clients = await local.Clients.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var products = await local.Products.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var locations = await local.Locations.AsNoTracking().ToListAsync();

            // Una SQLite vieja puede no tener la tabla Users (se creó en la migración
            // MultiUserStockCost); en ese caso simplemente no hay usuarios que subir.
            List<Core.Entities.User> users;
            try { users = await local.Users.AsNoTracking().ToListAsync(); }
            catch { users = new List<Core.Entities.User>(); }
            var orders = await local.Orders.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var items = await local.OrderItems.AsNoTracking().ToListAsync();

            await UpsertAllAsync(remote, clients, c => c.Id);
            await UpsertAllAsync(remote, products, p => p.Id);
            await UpsertAllAsync(remote, locations, l => l.Id);
            await UpsertAllAsync(remote, users, u => u.Id);
            await UpsertAllAsync(remote, orders, o =>
            {
                // Nav props locales no deben viajar: EF intentaría insertar duplicados.
                o.Client = null;
                o.Location = null;
                o.Items = new List<Core.Entities.OrderItem>();
                return o.Id;
            });
            await UpsertAllAsync(remote, items, i =>
            {
                i.Product = null;
                return i.Id;
            });

            await tx.CommitAsync();

            AppLog.Information(
                "Carga inicial al servidor completada: {Clients} clientes, {Products} productos, {Locations} ubicaciones, {Users} usuarios, {Orders} órdenes, {Items} items",
                clients.Count, products.Count, locations.Count, users.Count, orders.Count, items.Count);
        }

        private static async Task UpsertAllAsync<T>(AlquitelDbContext remote, List<T> rows, Func<T, Guid> keyOf)
            where T : class
        {
            if (rows.Count == 0) return;

            // Un solo round-trip para saber qué IDs ya existen: el FirstOrDefault por
            // fila era un N+1 contra el servidor (5.000 items = 5.000 viajes a sa-east-1).
            var ids = rows.Select(keyOf).ToList();
            var existingIds = (await remote.Set<T>().IgnoreQueryFilters()
                    .Where(e => ids.Contains(EF.Property<Guid>(e, "Id")))
                    .Select(e => EF.Property<Guid>(e, "Id"))
                    .ToListAsync())
                .ToHashSet();

            foreach (var row in rows)
            {
                var id = keyOf(row);
                if (!existingIds.Contains(id))
                {
                    remote.Set<T>().Add(row);
                }
                else
                {
                    // Attach + marcar modificado: actualiza sin releer la fila remota.
                    var entry = remote.Set<T>().Attach(row);
                    entry.State = EntityState.Modified;
                }
            }
            await remote.SaveChangesAsync();
            remote.ChangeTracker.Clear();
        }
    }
}
