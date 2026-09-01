using System.Text;
using System.Text.Json;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Alquitel.Infrastructure.Tests;

public sealed class LocalPersistenceIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"alquitel_local_persistence_{Guid.NewGuid():N}");

    public LocalPersistenceIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DraftProtegePiiYSePuedeRecuperar()
    {
        var service = new DraftService(Path.Combine(_root, "drafts"));
        var order = CreateOrder("Cliente Secreto", "20-12345678-3", "dato privado");

        await service.SaveDraftAsync(order, order.Items);

        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "drafts"), "*.json"));
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.DoesNotContain("Cliente Secreto", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("20-12345678-3", Encoding.UTF8.GetString(bytes));
        var loaded = await service.LoadDraftAsync(file);
        Assert.Equal("Cliente Secreto", loaded!.ClientName);
        Assert.Equal("dato privado", loaded.Comments);
    }

    [Fact]
    public async Task DraftLegadoSeMigraAlFormatoProtegidoDespuesDeLeerlo()
    {
        var folder = Path.Combine(_root, "drafts");
        var service = new DraftService(folder);
        var path = Path.Combine(folder, "draft_legacy.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new OrderDraft
        {
            Id = Guid.NewGuid(),
            ClientName = "Legado Visible",
        }));

        var loaded = await service.LoadDraftAsync(path);

        Assert.Equal("Legado Visible", loaded!.ClientName);
        Assert.DoesNotContain("Legado Visible", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DraftCorruptoSeMueveACuarentenaConMetadatos()
    {
        var folder = Path.Combine(_root, "drafts");
        var service = new DraftService(folder);
        var path = Path.Combine(folder, "draft_corrupt.json");
        await File.WriteAllTextAsync(path, "{\"ClientName\":\"PII truncada 20-12345678-3");

        Assert.Null(await service.LoadDraftAsync(path));

        Assert.False(File.Exists(path));
        var quarantine = Path.Combine(folder, "Quarantine");
        var quarantined = Assert.Single(Directory.GetFiles(quarantine, "*.bad"));
        Assert.DoesNotContain("PII truncada", await File.ReadAllTextAsync(quarantined));
        var metadata = Assert.Single(Directory.GetFiles(quarantine, "*.metadata.json"));
        Assert.Contains("corrupt_or_unreadable", await File.ReadAllTextAsync(metadata));
    }

    [Fact]
    public async Task DraftsConcurrentesSiempreDejanUnArchivoCompleto()
    {
        var folder = Path.Combine(_root, "drafts");
        var service = new DraftService(folder);
        var order = CreateOrder("Cliente", "", "inicio");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            var copy = CreateOrder("Cliente", "", $"versión {i}", order.Id);
            await service.SaveDraftAsync(copy, copy.Items);
        }));

        var file = Assert.Single(Directory.GetFiles(folder, "*.json"));
        Assert.NotNull(await service.LoadDraftAsync(file));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public async Task OutboxProtegePiiYReconoceReintentoYaAplicado()
    {
        var folder = Path.Combine(_root, "outbox");
        var order = CreateOrder("Cliente Secreto", "20-12345678-3", "mismo contenido");
        var latest = CreateOrder("Cliente Secreto", "20-12345678-3", "mismo contenido", order.Id);
        latest.RowVersion = Guid.NewGuid();
        var persistence = new StubPersistence(new OrderPersistOutcome(
            OrderPersistStatus.Saved, latest.RowVersion, latest.BudgetNumber));
        using var service = new OrderOutboxService(persistence, folder);

        Assert.True(service.Enqueue(order));
        var file = Assert.Single(Directory.GetFiles(folder, "order_*.json"));
        Assert.DoesNotContain("Cliente Secreto", await File.ReadAllTextAsync(file));

        Assert.Equal(1, await service.RetryPendingAsync());
        Assert.Equal(0, service.PendingCount);
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "Quarantine"), "*.bad"));
    }

    [Fact]
    public async Task OutboxCorruptoOEnConflictoSeConservaEnCuarentena()
    {
        var folder = Path.Combine(_root, "outbox");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "order_corrupt.json"), new byte[] { 9, 8, 7 });
        var order = CreateOrder("Cliente", "", "local");
        var latest = CreateOrder("Cliente", "", "remoto", order.Id);
        var persistence = new StubPersistence(new OrderPersistOutcome(
            OrderPersistStatus.Conflict,
            Conflict: new OrderConflictDetails(
                order.Id, order.RowVersion, latest.RowVersion, new[] { "Comentarios" }, latest)));
        using var service = new OrderOutboxService(persistence, folder);
        Assert.True(service.Enqueue(order));

        Assert.Equal(0, await service.RetryPendingAsync());

        Assert.Equal(0, service.PendingCount);
        var quarantine = Path.Combine(folder, "Quarantine");
        Assert.Equal(2, Directory.GetFiles(quarantine, "*.bad").Length);
        Assert.Equal(2, Directory.GetFiles(quarantine, "*.metadata.json").Length);
    }

    [Fact]
    public async Task OutboxNoBorraUnaVersionNuevaEncoladaDuranteElReintento()
    {
        var folder = Path.Combine(_root, "outbox");
        var first = CreateOrder("Cliente", "", "versión inicial");
        var newer = CreateOrder("Cliente", "", "versión nueva", first.Id);
        var persistence = new BlockingPersistence();
        using var service = new OrderOutboxService(persistence, folder);
        Assert.True(service.Enqueue(first));

        var retry = service.RetryPendingAsync();
        await persistence.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.Enqueue(newer));
        persistence.ReleaseFirstCall.SetResult();
        Assert.Equal(1, await retry);

        Assert.Equal(1, service.PendingCount);
        Assert.Equal(1, await service.RetryPendingAsync());
        Assert.Equal(0, service.PendingCount);
        Assert.Equal(new[] { "versión inicial", "versión nueva" }, persistence.Comments);
    }

    [Fact]
    public void OutboxInformaCuandoNoPuedeAsegurarLaCopiaDurable()
    {
        var folder = Path.Combine(_root, "outbox-unwritable");
        using var service = new OrderOutboxService(
            new StubPersistence(new OrderPersistOutcome(OrderPersistStatus.Saved)), folder);
        Directory.Delete(folder, recursive: true);
        File.WriteAllText(folder, "bloqueo intencional");

        Assert.False(service.Enqueue(CreateOrder("Cliente", "", "pedido")));
    }

    [Fact]
    public void BackupProtegidoSeRestauraConSnapshotDeSeguridad()
    {
        var dbPath = Path.Combine(_root, "data.db");
        var backups = Path.Combine(_root, "backups");
        CreateDatabase(dbPath, "PII original 20-12345678-3");
        using var service = new DatabaseBackupService(dbPath, backups);

        var backup = service.CreateBackupNow();

        Assert.NotNull(backup);
        Assert.DoesNotContain("PII original", File.ReadAllText(backup!.FilePath));
        SetDatabaseValue(dbPath, "valor modificado");
        service.RestoreBackup(backup.FilePath);
        Assert.Equal("valor modificado", ReadDatabaseValue(dbPath));
        var pending = Path.Combine(backups, "pending_restore.alq");
        Assert.DoesNotContain("PII original", File.ReadAllText(pending));
        using var applicationLease = service.AcquireApplicationDatabaseLease();
        Assert.True(service.ApplyPendingRestoreAtStartup(applicationLease));
        Assert.Equal("PII original 20-12345678-3", ReadDatabaseValue(dbPath));
        var safety = Assert.Single(Directory.GetFiles(backups, "Alquitel_PreRestore_*.db"));
        Assert.DoesNotContain("valor modificado", File.ReadAllText(safety));
        Assert.Equal(2, service.GetAvailableBackups().Count);
    }

    [Fact]
    public void BackupLegadoValidoSeMigraYSeRestaura()
    {
        var dbPath = Path.Combine(_root, "data.db");
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backups);
        CreateDatabase(dbPath, "valor legado");
        var legacy = Path.Combine(backups, "Alquitel_Backup_legacy.db");
        File.Copy(dbPath, legacy);
        SetDatabaseValue(dbPath, "valor nuevo");
        using var service = new DatabaseBackupService(dbPath, backups);

        service.RestoreBackup(legacy);
        Assert.Equal("valor nuevo", ReadDatabaseValue(dbPath));
        using var applicationLease = service.AcquireApplicationDatabaseLease();
        Assert.True(service.ApplyPendingRestoreAtStartup(applicationLease));

        Assert.Equal("valor legado", ReadDatabaseValue(dbPath));
        Assert.DoesNotContain("valor legado", File.ReadAllText(legacy));
    }

    [Fact]
    public void BackupCorruptoSeCuarentenaSinAlterarLaBaseActual()
    {
        var dbPath = Path.Combine(_root, "data.db");
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backups);
        CreateDatabase(dbPath, "base intacta");
        var corrupt = Path.Combine(backups, "Alquitel_Backup_corrupt.db");
        File.WriteAllBytes(corrupt, new byte[] { 1, 3, 3, 7 });
        using var service = new DatabaseBackupService(dbPath, backups);

        Assert.ThrowsAny<Exception>(() => service.RestoreBackup(corrupt));

        Assert.Equal("base intacta", ReadDatabaseValue(dbPath));
        Assert.False(File.Exists(corrupt));
        Assert.Single(Directory.GetFiles(Path.Combine(backups, "Quarantine"), "*.bad"));
    }

    [Fact]
    public void RetencionConservaSoloLosBackupsMasRecientes()
    {
        var dbPath = Path.Combine(_root, "data.db");
        var backups = Path.Combine(_root, "backups");
        CreateDatabase(dbPath, "dato");
        using var service = new DatabaseBackupService(dbPath, backups, retention: 2);

        for (var i = 0; i < 4; i++)
        {
            SetDatabaseValue(dbPath, $"dato {i}");
            Assert.NotNull(service.CreateBackupNow());
        }

        Assert.Equal(2, service.GetAvailableBackups().Count);
        Assert.Empty(Directory.GetFiles(backups, ".snapshot_*.tmp"));
    }

    [Fact]
    public void LeaseDeAplicacionImpideDosInstanciasSobreLaMismaBase()
    {
        var dbPath = Path.Combine(_root, "data.db");
        var backups = Path.Combine(_root, "backups");
        CreateDatabase(dbPath, "dato");
        using var first = new DatabaseBackupService(dbPath, backups);
        using var second = new DatabaseBackupService(dbPath, backups);
        using var lease = first.AcquireApplicationDatabaseLease();

        Assert.Throws<InvalidOperationException>(() => second.AcquireApplicationDatabaseLease());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Order CreateOrder(
        string clientName, string cuit, string comments, Guid? id = null)
    {
        var client = new Client { Id = Guid.NewGuid(), CompanyName = clientName, Cuit = cuit };
        var location = new Location { Id = Guid.NewGuid(), Name = "Lugar" };
        return new Order
        {
            Id = id ?? Guid.NewGuid(),
            BudgetNumber = "100",
            Client = client,
            ClientId = client.Id,
            Location = location,
            LocationId = location.Id,
            Comments = comments,
            RowVersion = Guid.NewGuid(),
            Items =
            {
                new OrderItem
                {
                    Id = Guid.NewGuid(),
                    ProductId = Guid.NewGuid(),
                    Quantity = 1,
                    Dias = 1,
                    UnitPrice = 100,
                    DescriptionSnapshot = "Producto",
                }
            }
        };
    }

    private sealed class StubPersistence : IOrderPersistenceService
    {
        private readonly OrderPersistOutcome _outcome;
        public StubPersistence(OrderPersistOutcome outcome) => _outcome = outcome;

        public Task<OrderPersistOutcome> PersistAsync(
            Order order,
            OrderConflictResolution resolution = OrderConflictResolution.Reject,
            Guid? operationId = null,
            CancellationToken cancellationToken = default) => Task.FromResult(_outcome);
    }

    private sealed class BlockingPersistence : IOrderPersistenceService
    {
        private int _calls;
        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string?> Comments { get; } = new();

        public async Task<OrderPersistOutcome> PersistAsync(
            Order order,
            OrderConflictResolution resolution = OrderConflictResolution.Reject,
            Guid? operationId = null,
            CancellationToken cancellationToken = default)
        {
            Comments.Add(order.Comments);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallStarted.SetResult();
                await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
            }

            return new OrderPersistOutcome(OrderPersistStatus.Saved, Guid.NewGuid(), order.BudgetNumber);
        }
    }

    private static void CreateDatabase(string path, string value)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Secrets(Value TEXT NOT NULL); INSERT INTO Secrets VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void SetDatabaseValue(string path, string value)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Secrets SET Value = $value";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string ReadDatabaseValue(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Secrets";
        return (string)command.ExecuteScalar()!;
    }
}
