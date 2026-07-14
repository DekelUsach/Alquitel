using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Persistence;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Resumen semanal ("el papelito del lunes"): un .docx corto con las métricas de la
    /// semana anterior, generado con OpenXML puro (sin Word, sin COM, sin plantilla).
    /// Lo dispara MainViewModel en el primer arranque de cada semana; queda en
    /// <see cref="AppPaths.SummariesFolder"/>.
    /// </summary>
    public class WeeklySummaryService : IWeeklySummaryService
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;

        public WeeklySummaryService(IDbContextFactory<AlquitelDbContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
        }

        public async Task<string> GenerateAsync(DateTime weekStart, DateTime weekEndExclusive)
        {
            // ── Métricas de la semana anterior ───────────────────────
            using var db = await _dbContextFactory.CreateDbContextAsync();

            // CreatedDate se guarda en UTC; media día de margen a cada lado cubre el
            // desfase horario sin complicar un reporte informativo.
            var utcStart = weekStart.AddHours(-12);
            var utcEnd = weekEndExclusive.AddHours(12);

            var weekOrders = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                .Include(o => o.Client)
                .Include(o => o.Items)
                .Where(o => o.CreatedDate >= utcStart && o.CreatedDate < utcEnd)
                .ToListAsync();
            weekOrders = weekOrders
                .Where(o => o.CreatedDate.ToLocalTime().Date >= weekStart &&
                            o.CreatedDate.ToLocalTime().Date < weekEndExclusive)
                .ToList();

            int emitted = weekOrders.Count;
            int approved = weekOrders.Count(o => o.Status == OrderStatus.Approved
                                              || o.Status == OrderStatus.SentToOF
                                              || o.Status == OrderStatus.SentToOT);
            decimal totalAmount = weekOrders.Sum(o => o.GrandTotal);
            decimal approvedAmount = weekOrders
                .Where(o => o.Status is OrderStatus.Approved or OrderStatus.SentToOF or OrderStatus.SentToOT)
                .Sum(o => o.GrandTotal);

            var topProducts = weekOrders
                .SelectMany(o => o.Items)
                .GroupBy(i => Alquitel.Core.Parsing.TagParser.StripTags(i.DescriptionSnapshot) is string s &&
                              !string.IsNullOrWhiteSpace(s) ? s : "(sin descripción)")
                .Select(g => (Name: g.Key, Qty: g.Sum(i => i.Quantity)))
                .OrderByDescending(t => t.Qty)
                .Take(5)
                .ToList();

            // Eventos de los próximos 7 días (para no llegar tarde a ninguno).
            var today = DateTime.Today;
            var upcoming = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                .Include(o => o.Client)
                .Include(o => o.Location)
                .Where(o => o.EventDate != null &&
                            o.EventDate >= today && o.EventDate < today.AddDays(7) &&
                            o.Status != OrderStatus.Rejected && o.Status != OrderStatus.Archived)
                .OrderBy(o => o.EventDate)
                .Take(10)
                .ToListAsync();

            // ── Documento ────────────────────────────────────────────
            string fileName = $"Resumen semanal {weekStart:yyyy-MM-dd}.docx";
            string outputPath = Path.Combine(AppPaths.SummariesFolder, fileName);

            await Task.Run(() => WriteDocument(outputPath, weekStart, weekEndExclusive,
                emitted, approved, totalAmount, approvedAmount, topProducts, upcoming));

            AppLog.Information("Resumen semanal generado: {Path}", outputPath);
            return outputPath;
        }

        private static void WriteDocument(string outputPath, DateTime weekStart, DateTime weekEndExclusive,
            int emitted, int approved, decimal totalAmount, decimal approvedAmount,
            List<(string Name, int Qty)> topProducts, List<Order> upcoming)
        {
            using var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new W.Document(new W.Body());
            var body = mainPart.Document.Body!;

            var weekEndInclusive = weekEndExclusive.AddDays(-1);
            body.Append(Heading("GRUPO ALQUITEL — Resumen semanal"));
            body.Append(Paragraph($"Semana del {weekStart:dd/MM/yyyy} al {weekEndInclusive:dd/MM/yyyy}", italic: true));
            body.Append(Paragraph(""));

            body.Append(SubHeading("Presupuestos"));
            body.Append(Bullet($"Emitidos: {emitted}"));
            body.Append(Bullet($"Aprobados / en curso: {approved}"));
            body.Append(Bullet($"Monto total presupuestado: {totalAmount:C0}"));
            body.Append(Bullet($"Monto aprobado / en curso: {approvedAmount:C0}"));
            body.Append(Paragraph(""));

            body.Append(SubHeading("Equipos más pedidos de la semana"));
            if (topProducts.Count == 0)
                body.Append(Paragraph("Sin movimientos esta semana.", italic: true));
            foreach (var (name, qty) in topProducts)
                body.Append(Bullet($"{name} — {qty} unidad(es)"));
            body.Append(Paragraph(""));

            body.Append(SubHeading("Eventos de los próximos 7 días"));
            if (upcoming.Count == 0)
                body.Append(Paragraph("No hay eventos agendados para los próximos 7 días.", italic: true));
            foreach (var o in upcoming)
            {
                string client = o.Client?.CompanyName ?? "(sin cliente)";
                string place = string.IsNullOrWhiteSpace(o.Location?.Name) ? "" : $" · {o.Location!.Name}";
                body.Append(Bullet($"{o.EventDate:dd/MM} — {client}{place} (presup. {o.BudgetNumber})"));
            }

            body.Append(Paragraph(""));
            body.Append(Paragraph($"Generado automáticamente el {DateTime.Now:dd/MM/yyyy HH:mm}.", italic: true));

            mainPart.Document.Save();
        }

        // ── Helpers de párrafos ──────────────────────────────────────

        private static W.Paragraph Heading(string text) => new(
            new W.ParagraphProperties(new W.SpacingBetweenLines { After = "160" }),
            new W.Run(
                new W.RunProperties(new W.Bold(), new W.FontSize { Val = "36" }),
                new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

        private static W.Paragraph SubHeading(string text) => new(
            new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "120", After = "80" }),
            new W.Run(
                new W.RunProperties(new W.Bold(), new W.FontSize { Val = "26" }),
                new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

        private static W.Paragraph Paragraph(string text, bool italic = false)
        {
            var props = new W.RunProperties(new W.FontSize { Val = "22" });
            if (italic) props.Append(new W.Italic());
            return new W.Paragraph(new W.Run(props,
                new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        }

        private static W.Paragraph Bullet(string text) => new(
            new W.ParagraphProperties(new W.Indentation { Left = "360" }),
            new W.Run(
                new W.RunProperties(new W.FontSize { Val = "22" }),
                new W.Text("• " + text) { Space = SpaceProcessingModeValues.Preserve }));
    }
}
