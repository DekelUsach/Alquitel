using System;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación EF de <see cref="IOrderPersistenceService"/>. Lógica movida tal
    /// cual desde BudgetBuilderViewModel.PersistOrderAsync/ResolveClientIdAsync, más:
    /// - Reintento con renumeración ante colisión del índice único de BudgetNumber
    ///   (dos usuarios creando presupuestos a la vez sobre la base compartida).
    /// - Concurrencia optimista vía Order.RowVersion (edición simultánea de la misma orden).
    /// </summary>
    public class OrderPersistenceService : IOrderPersistenceService
    {
        private const int MaxBudgetNumberRetries = 3;

        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IOrderAuditService _audit;

        public OrderPersistenceService(IDbContextFactory<AlquitelDbContext> dbContextFactory, IOrderAuditService audit)
        {
            _dbContextFactory = dbContextFactory;
            _audit = audit;
        }

        public async Task<OrderPersistResult> PersistAsync(Order order, bool forceOverwrite = false)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await PersistOnceAsync(order, forceOverwrite);
                }
                catch (DbUpdateException ex) when (attempt < MaxBudgetNumberRetries && IsBudgetNumberCollision(ex))
                {
                    // Otro usuario tomó el mismo número entre la asignación y el guardado.
                    // Se renumera (misma serie si es versión, próximo serial si no) y se
                    // reintenta. El VM detecta el cambio comparando BudgetNumber pre/post.
                    var oldNumber = order.BudgetNumber;
                    order.BudgetNumber = await NextAvailableNumberAsync(order.BudgetNumber);
                    AppLog.Warning(
                        "Colisión de número de presupuesto {Old} (índice único): renumerado a {New}, reintento {Attempt}",
                        oldNumber, order.BudgetNumber, attempt + 1);
                }
                catch (Exception ex)
                {
                    AppLog.Error(ex, "PersistAsync failed for order {OrderId}", order.Id);
                    return OrderPersistResult.Error;
                }
            }
        }

        private async Task<OrderPersistResult> PersistOnceAsync(Order order, bool forceOverwrite)
        {
            using var db = await _dbContextFactory.CreateDbContextAsync();

            // En modo servidor el DbContext trae EnableRetryOnFailure: abrir una
            // transacción a mano sin pasar por la estrategia de ejecución hace que EF
            // lance InvalidOperationException y ningún presupuesto se guarde.
            var strategy = db.Database.CreateExecutionStrategy();

            // El cuerpo se puede re-ejecutar tras un corte de red. RowVersion se muta
            // dentro (rotación del token de concurrencia): si no se restaura el valor
            // con el que se cargó la orden, el segundo intento se compara contra un
            // token que la base nunca vio y devuelve un Conflict inventado.
            var loadedRowVersion = order.RowVersion;

            var (result, existed) = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                order.RowVersion = loadedRowVersion;
                return await PersistCoreAsync(db, order, forceOverwrite);
            });

            if (result == OrderPersistResult.Saved)
            {
                // Bitácora: fuera de la transacción y fuera del reintento (un fallo de
                // auditoría no revierte la orden, y un reintento no la duplica).
                await _audit.LogAsync(order.Id,
                    existed ? "Editado" : "Creado",
                    $"Presupuesto {order.BudgetNumber} · {order.Items.Count} ítem(s) · total {order.GrandTotal:C}");
            }

            return result;
        }

        private async Task<(OrderPersistResult Result, bool OrderExisted)> PersistCoreAsync(
            AlquitelDbContext db, Order order, bool forceOverwrite)
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            // ── Location: find-or-create so Order.LocationId always references a real row.
            // A Guid.Empty FK violated the constraint and silently failed the whole persist. ──
            var locName = (order.Location?.Name ?? string.Empty).Trim();
            var location = await db.Locations.FirstOrDefaultAsync(l => l.Name == locName);
            if (location == null)
            {
                location = new Location { Name = locName };
                db.Locations.Add(location);
                await db.SaveChangesAsync();
            }

            // ── Client: reuse existing (by Id, then by CUIT) or create it.
            // A client typed manually never existed in the DB and broke the FK. ──
            var clientId = await ResolveClientIdAsync(db, order);
            var locationId = location.Id;

            var orderExists = await db.Orders.AnyAsync(o => o.Id == order.Id);

            if (!orderExists)
            {
                var orderToSave = new Order
                {
                    Id = order.Id,
                    BudgetNumber = order.BudgetNumber,
                    AdminName = order.AdminName,
                    CreatedByUserId = order.CreatedByUserId,
                    ClientId = clientId,
                    LocationId = locationId,
                    CreatedDate = order.CreatedDate,
                    EventDate = order.EventDate,
                    EventEndDate = order.EventEndDate,
                    Status = order.Status,
                    Comments = order.Comments,
                    DiscountPercent = order.DiscountPercent,
                    DiscountAmount = order.DiscountAmount,
                    AddVat = order.AddVat,
                    RowVersion = order.RowVersion == Guid.Empty ? Guid.NewGuid() : order.RowVersion,
                };
                db.Orders.Add(orderToSave);
                await db.SaveChangesAsync();
                order.RowVersion = orderToSave.RowVersion;

                foreach (var item in order.Items)
                {
                    db.OrderItems.Add(CloneForInsert(item, orderToSave.Id, keepId: true));
                }
                await db.SaveChangesAsync();
            }
            else
            {
                var tracked = await db.Orders.FindAsync(order.Id);
                if (tracked != null)
                {
                    // ── Concurrencia optimista: si la fila cambió desde que este usuario
                    // la cargó, no pisar en silencio. Guid.Empty = fila legada sin token.
                    if (!forceOverwrite &&
                        tracked.RowVersion != Guid.Empty &&
                        order.RowVersion != Guid.Empty &&
                        tracked.RowVersion != order.RowVersion)
                    {
                        await tx.RollbackAsync();
                        AppLog.Warning(
                            "Conflicto de concurrencia en orden {OrderId}: RowVersion base {Db} ≠ cargada {Loaded}",
                            order.Id, tracked.RowVersion, order.RowVersion);
                        return (OrderPersistResult.Conflict, true);
                    }

                    tracked.BudgetNumber = order.BudgetNumber;
                    tracked.AdminName = order.AdminName;
                    // No pisar al creador original al editar una orden ajena.
                    tracked.CreatedByUserId ??= order.CreatedByUserId;
                    tracked.ClientId = clientId;
                    tracked.LocationId = locationId;
                    tracked.EventDate = order.EventDate;
                    tracked.EventEndDate = order.EventEndDate;
                    tracked.Status = order.Status;
                    tracked.Comments = order.Comments;
                    tracked.DiscountPercent = order.DiscountPercent;
                    tracked.DiscountAmount = order.DiscountAmount;
                    tracked.AddVat = order.AddVat;

                    // Rotar el token: los guardados posteriores de OTRO usuario con la
                    // versión vieja caerán en Conflict. El de este usuario sigue en sync.
                    tracked.RowVersion = Guid.NewGuid();
                    order.RowVersion = tracked.RowVersion;
                }

                var oldItems = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync();
                db.OrderItems.RemoveRange(oldItems);
                await db.SaveChangesAsync();

                foreach (var item in order.Items)
                {
                    db.OrderItems.Add(CloneForInsert(item, order.Id, keepId: false));
                }
                await db.SaveChangesAsync();
            }

            await tx.CommitAsync();
            AppLog.Information("Order persisted: {OrderId} ({Budget})", order.Id, order.BudgetNumber);
            return (OrderPersistResult.Saved, orderExists);
        }

        /// <summary>
        /// True si la excepción es una violación del índice único de Orders.BudgetNumber
        /// (SQLite error 19 / PostgreSQL 23505).
        /// </summary>
        private static bool IsBudgetNumberCollision(DbUpdateException ex)
        {
            return ex.InnerException switch
            {
                Microsoft.Data.Sqlite.SqliteException sq =>
                    sq.SqliteErrorCode == 19 &&
                    sq.Message.Contains("BudgetNumber", StringComparison.OrdinalIgnoreCase),
                Npgsql.PostgresException pg =>
                    pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation &&
                    (pg.ConstraintName?.Contains("BudgetNumber", StringComparison.OrdinalIgnoreCase) ?? false),
                _ => false,
            };
        }

        /// <summary>
        /// Próximo número libre tras una colisión: si el número era una versión
        /// ("31294/2") se calcula la próxima versión de esa serie; si era un serial
        /// se toma el próximo serial global.
        /// </summary>
        private async Task<string> NextAvailableNumberAsync(string collidedNumber)
        {
            using var db = await _dbContextFactory.CreateDbContextAsync();
            var numbers = await db.Orders.IgnoreQueryFilters().AsNoTracking()
                .Select(o => o.BudgetNumber)
                .ToListAsync();

            return BudgetNumberHelper.VersionPart(collidedNumber) > 1
                ? BudgetNumberHelper.NextVersion(collidedNumber, numbers)
                : BudgetNumberHelper.NextSerial(numbers);
        }

        private static OrderItem CloneForInsert(OrderItem item, Guid orderId, bool keepId) => new()
        {
            Id = keepId ? item.Id : Guid.NewGuid(),
            OrderId = orderId,
            ProductId = item.ProductId,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            Dias = item.Dias,
            TechnicalNotes = item.TechnicalNotes,
            ImagePath = item.ImagePath,
            CustomFieldsJson = item.CustomFieldsJson,
            DescriptionSnapshot = item.DescriptionSnapshot,
            RequestedMeasure = item.RequestedMeasure,
        };

        /// <summary>
        /// Returns the Id of a Client row guaranteed to exist in the DB for the order:
        /// the tracked client if already persisted, an existing client with the same CUIT,
        /// or a newly inserted row built from the manually typed data.
        /// </summary>
        private static async Task<Guid> ResolveClientIdAsync(AlquitelDbContext db, Order order)
        {
            var client = order.Client ?? new Client();

            if (client.Id != Guid.Empty &&
                await db.Clients.IgnoreQueryFilters().AnyAsync(c => c.Id == client.Id))
                return client.Id;

            if (!string.IsNullOrWhiteSpace(client.Cuit))
            {
                var cuit = client.Cuit.Trim();
                var byCuit = await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Cuit == cuit);
                if (byCuit != null) return byCuit.Id;
            }

            var newClient = new Client
            {
                Id = client.Id == Guid.Empty ? Guid.NewGuid() : client.Id,
                CompanyName = client.CompanyName?.Trim() ?? string.Empty,
                Cuit = client.Cuit?.Trim() ?? string.Empty,
                ContactName = client.ContactName,
                Phone = client.Phone,
                Email = client.Email,
            };
            db.Clients.Add(newClient);
            await db.SaveChangesAsync();
            AppLog.Information("Client auto-created from budget: {Company} ({Cuit})", newClient.CompanyName, newClient.Cuit);
            return newClient.Id;
        }
    }
}
