using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Infrastructure.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace Alquitel.Infrastructure.Tests;

public sealed class OrderConcurrencyIntegrationTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"alquitel_concurrency_{Guid.NewGuid():N}.db");
    private PooledDbContextFactory<AlquitelDbContext> _factory = null!;
    private CurrentUserService _currentUser = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AlquitelDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        _factory = new PooledDbContextFactory<AlquitelDbContext>(options);

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _currentUser = new CurrentUserService();
        _currentUser.SetCurrentUser(new User
        {
            Id = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"),
            Name = "Test User",
        });
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RowVersionEstaConfiguradoComoTokenYEvitaLaCarreraEntreContextos()
    {
        var order = await SeedOrderAsync();
        await using var firstDb = await _factory.CreateDbContextAsync();
        await using var secondDb = await _factory.CreateDbContextAsync();
        var first = await firstDb.Orders.SingleAsync(o => o.Id == order.Id);
        var second = await secondDb.Orders.SingleAsync(o => o.Id == order.Id);

        Assert.True(firstDb.Model.FindEntityType(typeof(Order))!
            .FindProperty(nameof(Order.RowVersion))!.IsConcurrencyToken);

        first.Comments = "Primera edición";
        first.RowVersion = Guid.NewGuid();
        await firstDb.SaveChangesAsync();

        second.Comments = "Edición obsoleta";
        second.RowVersion = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
    }

    [Fact]
    public async Task PersistirUnaVersionObsoletaDevuelveSnapshotYNoAuditaElConflicto()
    {
        var order = await SeedOrderAsync();
        var service = new OrderPersistenceService(_factory, _currentUser);
        var first = await LoadOrderAsync(order.Id);
        var stale = await LoadOrderAsync(order.Id);

        first.Comments = "Guardado por Ana";
        var saved = await service.PersistAsync(first);
        stale.Comments = "Guardado por Beto";
        var conflict = await service.PersistAsync(stale);

        Assert.Equal(OrderPersistStatus.Saved, saved.Status);
        Assert.Equal(OrderPersistStatus.Conflict, conflict.Status);
        Assert.NotNull(conflict.Conflict);
        Assert.Equal("Guardado por Ana", conflict.Conflict!.LatestOrder.Comments);
        Assert.Contains("Comentarios", conflict.Conflict.ChangedFields);
        Assert.Equal(stale.RowVersion, conflict.Conflict.ExpectedRowVersion);
        Assert.Equal(saved.PersistedRowVersion, conflict.Conflict.ActualRowVersion);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.OrderAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task SobrescrituraExplicitaUsaLaUltimaVersionYRotaElToken()
    {
        var order = await SeedOrderAsync();
        var service = new OrderPersistenceService(_factory, _currentUser);
        var first = await LoadOrderAsync(order.Id);
        var stale = await LoadOrderAsync(order.Id);

        first.Comments = "Primera edición";
        var firstResult = await service.PersistAsync(first);
        stale.Comments = "Sobrescritura elegida";
        var overwrite = await service.PersistAsync(
            stale, OrderConflictResolution.OverwriteLatest);

        Assert.Equal(OrderPersistStatus.Saved, overwrite.Status);
        Assert.NotEqual(firstResult.PersistedRowVersion, overwrite.PersistedRowVersion);
        Assert.Equal(overwrite.PersistedRowVersion, stale.RowVersion);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("Sobrescritura elegida", (await db.Orders.FindAsync(order.Id))!.Comments);
        Assert.Equal(2, await db.OrderAuditEvents.CountAsync());
    }

    [Fact]
    public async Task UnTercerEscritorInvalidaLaVersionTomadaParaSobrescribir()
    {
        var order = await SeedOrderAsync();
        await using var overwriteDb = await _factory.CreateDbContextAsync();
        var overwrite = await overwriteDb.Orders.SingleAsync(o => o.Id == order.Id);

        await using (var firstDb = await _factory.CreateDbContextAsync())
        {
            var first = await firstDb.Orders.SingleAsync(o => o.Id == order.Id);
            first.Comments = "Primera edición";
            first.RowVersion = Guid.NewGuid();
            await firstDb.SaveChangesAsync();
        }

        var versionSelectedForOverwrite = await LoadOrderAsync(order.Id);
        overwriteDb.Entry(overwrite).Property(o => o.RowVersion).OriginalValue =
            versionSelectedForOverwrite.RowVersion;
        overwrite.Comments = "Sobrescritura pendiente";
        overwrite.RowVersion = Guid.NewGuid();

        await using (var thirdDb = await _factory.CreateDbContextAsync())
        {
            var third = await thirdDb.Orders.SingleAsync(o => o.Id == order.Id);
            third.Comments = "Tercer escritor";
            third.RowVersion = Guid.NewGuid();
            await thirdDb.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => overwriteDb.SaveChangesAsync());

        await using var verificationDb = await _factory.CreateDbContextAsync();
        Assert.Equal("Tercer escritor", (await verificationDb.Orders.FindAsync(order.Id))!.Comments);
    }

    [Fact]
    public async Task CrearOrdenGuardaItemsTokenYAuditoriaEnLaMismaOperacion()
    {
        var existing = await SeedOrderAsync();
        var productId = existing.Items.Single().ProductId;
        var created = CreateDetachedOrder("200", productId);
        var service = new OrderPersistenceService(_factory, _currentUser);

        var result = await service.PersistAsync(created);

        Assert.Equal(OrderPersistStatus.Saved, result.Status);
        Assert.NotEqual(Guid.Empty, result.PersistedRowVersion);
        await using var db = await _factory.CreateDbContextAsync();
        var persisted = await db.Orders.Include(o => o.Items)
            .SingleAsync(o => o.Id == created.Id);
        Assert.Single(persisted.Items);
        Assert.Equal(result.PersistedRowVersion, persisted.RowVersion);
        var audit = await db.OrderAuditEvents.SingleAsync(e => e.OrderId == created.Id);
        Assert.Equal("Creado", audit.EventType);
    }

    [Fact]
    public async Task ColisionDeNumeroRenumeraSinDuplicarLaAuditoria()
    {
        var existing = await SeedOrderAsync(budgetNumber: "100");
        var created = CreateDetachedOrder("100", existing.Items.Single().ProductId);
        var service = new OrderPersistenceService(_factory, _currentUser);

        var result = await service.PersistAsync(created);

        Assert.Equal(OrderPersistStatus.Saved, result.Status);
        Assert.Equal("101", result.PersistedBudgetNumber);
        Assert.Equal("101", created.BudgetNumber);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.OrderAuditEvents.CountAsync(e => e.OrderId == created.Id));
        Assert.Equal(2, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task FalloDeAuditoriaRevierteOrdenEItemsCompletos()
    {
        var order = await SeedOrderAsync();
        var originalRowVersion = order.RowVersion;
        var edited = await LoadOrderAsync(order.Id);
        edited.Comments = "No debe persistir";
        edited.Items.Single().Quantity = 9;

        await using (var triggerDb = await _factory.CreateDbContextAsync())
        {
            await triggerDb.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER AbortOrderAudit BEFORE INSERT ON OrderAuditEvents " +
                "BEGIN SELECT RAISE(ABORT, 'audit blocked'); END;");
        }

        var service = new OrderPersistenceService(_factory, _currentUser);
        var result = await service.PersistAsync(edited);

        Assert.Equal(OrderPersistStatus.Error, result.Status);
        await using var db = await _factory.CreateDbContextAsync();
        var persisted = await db.Orders.AsNoTracking().Include(o => o.Items)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Null(persisted.Comments);
        Assert.Equal(1, persisted.Items.Single().Quantity);
        Assert.Equal(originalRowVersion, persisted.RowVersion);
        Assert.Empty(await db.OrderAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task CancelacionDuranteElGuardadoRevierteOrdenItemsYAuditoria()
    {
        var order = await SeedOrderAsync();
        var originalRowVersion = order.RowVersion;
        var edited = await LoadOrderAsync(order.Id);
        edited.Comments = "No debe persistir por cancelación";
        edited.Items.Single().Quantity = 7;
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelOnOrderUpdateInterceptor(cancellation);
        var options = new DbContextOptionsBuilder<AlquitelDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(interceptor)
            .Options;
        var cancellingFactory = new PooledDbContextFactory<AlquitelDbContext>(options);
        var service = new OrderPersistenceService(cancellingFactory, _currentUser);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PersistAsync(edited, cancellationToken: cancellation.Token));

        await using var db = await _factory.CreateDbContextAsync();
        var persisted = await db.Orders.AsNoTracking().Include(o => o.Items)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Null(persisted.Comments);
        Assert.Equal(1, persisted.Items.Single().Quantity);
        Assert.Equal(originalRowVersion, persisted.RowVersion);
        Assert.Empty(await db.OrderAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task CommitAmbiguoSeVerificaSinRepetirMutacionNiAuditoria()
    {
        var order = await SeedOrderAsync();
        var edited = await LoadOrderAsync(order.Id);
        edited.Comments = "Commit confirmado por verificación";
        var interceptor = new ThrowAfterFirstCommitInterceptor();
        var options = new DbContextOptionsBuilder<AlquitelDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(interceptor)
            .ReplaceService<IExecutionStrategyFactory, AmbiguousCommitExecutionStrategyFactory>()
            .Options;
        var ambiguousFactory = new PooledDbContextFactory<AlquitelDbContext>(options);
        var service = new OrderPersistenceService(ambiguousFactory, _currentUser);

        var result = await service.PersistAsync(edited);

        Assert.Equal(OrderPersistStatus.Saved, result.Status);
        Assert.True(interceptor.ThrewAfterCommit);
        await using var db = await _factory.CreateDbContextAsync();
        var persisted = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal("Commit confirmado por verificación", persisted.Comments);
        Assert.Equal(result.PersistedRowVersion, persisted.RowVersion);
        Assert.Equal(1, await db.OrderAuditEvents.CountAsync(e => e.OrderId == order.Id));
    }

    [Fact]
    public async Task CambioDeEstadoValidaPoliticaConcurrenciaEIdempotencia()
    {
        var order = await SeedOrderAsync();
        var service = new OrderStatusService(_factory, _currentUser);

        var invalid = await service.ChangeAsync(order.Id, order.RowVersion, OrderStatus.SentToOT);
        Assert.Equal(OrderPersistStatus.Error, invalid.Status);
        Assert.Equal("invalid_status_transition", invalid.ErrorCode);
        await using (var invalidDb = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(order.RowVersion, (await invalidDb.Orders.FindAsync(order.Id))!.RowVersion);
            Assert.Empty(await invalidDb.OrderAuditEvents.ToListAsync());
        }

        var approved = await service.ChangeAsync(order.Id, order.RowVersion, OrderStatus.Approved);
        Assert.Equal(OrderPersistStatus.Saved, approved.Status);
        Assert.NotEqual(order.RowVersion, approved.PersistedRowVersion);

        var stale = await service.ChangeAsync(order.Id, order.RowVersion, OrderStatus.Archived);
        Assert.Equal(OrderPersistStatus.Conflict, stale.Status);

        var idempotent = await service.ChangeAsync(
            order.Id, order.RowVersion, OrderStatus.Approved);
        Assert.Equal(OrderPersistStatus.Saved, idempotent.Status);
        Assert.Equal(approved.PersistedRowVersion, idempotent.PersistedRowVersion);

        await using var db = await _factory.CreateDbContextAsync();
        var persisted = (await db.Orders.FindAsync(order.Id))!;
        Assert.Equal(OrderStatus.Approved, persisted.Status);
        Assert.Equal(approved.PersistedRowVersion, persisted.RowVersion);
        Assert.Single(await db.OrderAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task RowVersionLegadoSePromueveUnaVezYLuegoProtegeLaFila()
    {
        var order = await SeedOrderAsync(Guid.Empty);
        var service = new OrderPersistenceService(_factory, _currentUser);
        var first = await LoadOrderAsync(order.Id);
        var stale = await LoadOrderAsync(order.Id);

        first.Comments = "Promoción";
        var promoted = await service.PersistAsync(first);
        stale.Comments = "Obsoleto";
        var conflict = await service.PersistAsync(stale);

        Assert.Equal(OrderPersistStatus.Saved, promoted.Status);
        Assert.NotEqual(Guid.Empty, promoted.PersistedRowVersion);
        Assert.Equal(OrderPersistStatus.Conflict, conflict.Status);
    }

    private async Task<Order> SeedOrderAsync(Guid? rowVersion = null, string? budgetNumber = null)
    {
        var client = new Client { CompanyName = "Cliente", Cuit = string.Empty };
        var location = new Location { Name = $"Lugar {Guid.NewGuid():N}" };
        var product = new Product { Description = "Pantalla", Category = "Visuales", BasePrice = 1000 };
        var order = new Order
        {
            BudgetNumber = budgetNumber ?? Guid.NewGuid().ToString("N")[..8],
            Client = client,
            ClientId = client.Id,
            Location = location,
            LocationId = location.Id,
            RowVersion = rowVersion ?? Guid.NewGuid(),
            Items =
            {
                new OrderItem
                {
                    Product = product,
                    ProductId = product.Id,
                    Quantity = 1,
                    Dias = 1,
                    UnitPrice = product.BasePrice,
                    DescriptionSnapshot = product.Description,
                }
            }
        };

        await using var db = await _factory.CreateDbContextAsync();
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return order;
    }

    private static Order CreateDetachedOrder(string budgetNumber, Guid productId)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            CompanyName = $"Cliente {Guid.NewGuid():N}",
            Cuit = string.Empty,
        };
        var location = new Location { Id = Guid.NewGuid(), Name = $"Lugar {Guid.NewGuid():N}" };
        return new Order
        {
            Id = Guid.NewGuid(),
            BudgetNumber = budgetNumber,
            Client = client,
            ClientId = client.Id,
            Location = location,
            LocationId = location.Id,
            RowVersion = Guid.Empty,
            Items =
            {
                new OrderItem
                {
                    Id = Guid.NewGuid(),
                    ProductId = productId,
                    Quantity = 1,
                    Dias = 1,
                    UnitPrice = 1000,
                    DescriptionSnapshot = "Pantalla",
                }
            }
        };
    }

    private async Task<Order> LoadOrderAsync(Guid id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Orders.AsNoTracking()
            .Include(o => o.Client)
            .Include(o => o.Location)
            .Include(o => o.Items)
            .SingleAsync(o => o.Id == id);
    }

    private sealed class CancelOnOrderUpdateInterceptor : DbCommandInterceptor
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelOnOrderUpdateInterceptor(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CancelIfOrderUpdate(command);

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CancelIfOrderUpdate(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CancelIfOrderUpdate(DbCommand command)
        {
            if (!command.CommandText.Contains("UPDATE \"Orders\"", StringComparison.Ordinal))
                return;

            _cancellation.Cancel();
            throw new OperationCanceledException(_cancellation.Token);
        }
    }

    private sealed class ThrowAfterFirstCommitInterceptor : DbTransactionInterceptor
    {
        private int _pendingFailure = 1;
        public bool ThrewAfterCommit { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _pendingFailure, 0) == 1)
            {
                ThrewAfterCommit = true;
                throw new SimulatedTransientException();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class AmbiguousCommitExecutionStrategyFactory : IExecutionStrategyFactory
    {
        private readonly ExecutionStrategyDependencies _dependencies;

        public AmbiguousCommitExecutionStrategyFactory(ExecutionStrategyDependencies dependencies) =>
            _dependencies = dependencies;

        public IExecutionStrategy Create() => new AmbiguousCommitExecutionStrategy(_dependencies);
    }

    private sealed class AmbiguousCommitExecutionStrategy : ExecutionStrategy
    {
        public AmbiguousCommitExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
        {
        }

        protected override bool ShouldRetryOn(Exception exception) =>
            exception is SimulatedTransientException;
    }

    private sealed class SimulatedTransientException : Exception;
}
