using System;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación EF de <see cref="IOrderPersistenceService"/>. Lógica movida tal
    /// cual desde BudgetBuilderViewModel.PersistOrderAsync/ResolveClientIdAsync.
    /// </summary>
    public class OrderPersistenceService : IOrderPersistenceService
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IOrderAuditService _audit;

        public OrderPersistenceService(IDbContextFactory<AlquitelDbContext> dbContextFactory, IOrderAuditService audit)
        {
            _dbContextFactory = dbContextFactory;
            _audit = audit;
        }

        public async Task<bool> PersistAsync(Order order)
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
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
                    };
                    db.Orders.Add(orderToSave);
                    await db.SaveChangesAsync();

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

                // Bitácora: fuera de la transacción (un fallo de auditoría no revierte la orden).
                await _audit.LogAsync(order.Id,
                    orderExists ? "Editado" : "Creado",
                    $"Presupuesto {order.BudgetNumber} · {order.Items.Count} ítem(s) · total {order.GrandTotal:C}");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "PersistAsync failed for order {OrderId}", order.Id);
                return false;
            }
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
