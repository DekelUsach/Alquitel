using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación en disco de <see cref="IOrderOutboxService"/> (patrón espejo de
    /// DraftService): un JSON por orden fallida en %AppData%\Alquitel\Outbox. Un timer
    /// reintenta cada 5 minutos; al lograr persistir, el archivo se elimina.
    /// </summary>
    public class OrderOutboxService : IOrderOutboxService, IDisposable
    {
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly string _outboxFolder;
        private readonly IOrderPersistenceService _persistence;
        private readonly SemaphoreSlim _retryLock = new(1, 1);
        private Timer? _timer;

        public OrderOutboxService(IOrderPersistenceService persistence)
            : this(persistence, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alquitel", "Outbox"))
        {
        }

        /// <summary>Carpeta inyectable para tests.</summary>
        public OrderOutboxService(IOrderPersistenceService persistence, string outboxFolder)
        {
            _persistence = persistence;
            _outboxFolder = outboxFolder;
            Directory.CreateDirectory(_outboxFolder);
        }

        /// <summary>Arranca el reintento periódico (primer intento al minuto, luego cada 5).</summary>
        public void Start()
        {
            _timer = new Timer(async _ =>
            {
                try { await RetryPendingAsync(); }
                catch (Exception ex) { AppLog.Warning(ex, "Outbox retry tick failed"); }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        }

        public int PendingCount
        {
            get
            {
                try { return Directory.EnumerateFiles(_outboxFolder, "order_*.json").Count(); }
                catch { return 0; }
            }
        }

        public void Enqueue(Order order)
        {
            try
            {
                // Clon plano: las nav props no viajan (Product dentro de los items pesa
                // y no hace falta; PersistAsync resuelve cliente/ubicación por nombre/CUIT).
                var snapshot = new Order
                {
                    Id = order.Id,
                    BudgetNumber = order.BudgetNumber,
                    AdminName = order.AdminName,
                    CreatedByUserId = order.CreatedByUserId,
                    CreatedDate = order.CreatedDate,
                    EventDate = order.EventDate,
                    EventEndDate = order.EventEndDate,
                    Status = order.Status,
                    Comments = order.Comments,
                    DiscountPercent = order.DiscountPercent,
                    DiscountAmount = order.DiscountAmount,
                    AddVat = order.AddVat,
                    RowVersion = order.RowVersion,
                    Client = order.Client == null ? null : new Client
                    {
                        Id = order.Client.Id,
                        CompanyName = order.Client.CompanyName,
                        Cuit = order.Client.Cuit,
                        ContactName = order.Client.ContactName,
                        Phone = order.Client.Phone,
                        Email = order.Client.Email,
                    },
                    Location = order.Location == null ? null : new Location
                    {
                        Id = order.Location.Id,
                        Name = order.Location.Name,
                    },
                    Items = order.Items.Select(i => new OrderItem
                    {
                        Id = i.Id,
                        OrderId = i.OrderId,
                        ProductId = i.ProductId,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice,
                        Dias = i.Dias,
                        TechnicalNotes = i.TechnicalNotes,
                        ImagePath = i.ImagePath,
                        CustomFieldsJson = i.CustomFieldsJson,
                        DescriptionSnapshot = i.DescriptionSnapshot,
                        RequestedMeasure = i.RequestedMeasure,
                    }).ToList(),
                };

                var json = JsonSerializer.Serialize(snapshot, WriteOptions);
                File.WriteAllText(PathFor(order.Id), json);
                AppLog.Information("Orden {OrderId} ({Budget}) encolada en outbox para reintento",
                    order.Id, order.BudgetNumber);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "No se pudo encolar la orden {OrderId} en el outbox", order.Id);
            }
        }

        public async Task<int> RetryPendingAsync()
        {
            if (!await _retryLock.WaitAsync(0)) return 0; // ya hay un reintento en curso
            try
            {
                int saved = 0;
                List<string> files;
                try { files = Directory.EnumerateFiles(_outboxFolder, "order_*.json").ToList(); }
                catch { return 0; }

                foreach (var file in files)
                {
                    Order? order;
                    try
                    {
                        order = JsonSerializer.Deserialize<Order>(await File.ReadAllTextAsync(file), ReadOptions);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning(ex, "Outbox: archivo corrupto {File} — se descarta", file);
                        TryDelete(file);
                        continue;
                    }
                    if (order == null) { TryDelete(file); continue; }

                    var result = await _persistence.PersistAsync(order);
                    switch (result)
                    {
                        case OrderPersistResult.Saved:
                            TryDelete(file);
                            saved++;
                            AppLog.Information("Outbox: orden {OrderId} ({Budget}) persistida en reintento",
                                order.Id, order.BudgetNumber);
                            break;
                        case OrderPersistResult.Conflict:
                            // Alguien más ya guardó una versión más nueva de esta orden:
                            // la copia encolada quedó obsoleta, no tiene sentido pisarla.
                            AppLog.Warning(
                                "Outbox: orden {OrderId} descartada por conflicto de concurrencia (hay versión más nueva en la base)",
                                order.Id);
                            TryDelete(file);
                            break;
                        case OrderPersistResult.Error:
                            // Sigue sin conexión (u otro fallo): queda para el próximo tick.
                            break;
                    }
                }
                return saved;
            }
            finally
            {
                _retryLock.Release();
            }
        }

        private string PathFor(Guid orderId) => Path.Combine(_outboxFolder, $"order_{orderId}.json");

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) { AppLog.Warning(ex, "Outbox: no se pudo borrar {File}", file); }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _retryLock.Dispose();
        }
    }
}
