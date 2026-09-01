using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Services;
using Alquitel.Infrastructure.Services.WordInterop;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.Security.Cryptography;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Alquitel.Infrastructure.Tests;

public sealed class DocumentGenerationIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"alquitel_documents_{Guid.NewGuid():N}");

    public DocumentGenerationIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task OpenXmlPublicaSinSobrescribirYDevuelveLaRutaReal()
    {
        var template = CreateTemplate("{{CLIENTE}}\n{{PRODUCTOS_AQUI}}");
        var requested = Path.Combine(_root, "presupuesto.docx");
        await File.WriteAllTextAsync(requested, "documento anterior");
        var service = new OpenXmlDocumentService();

        var result = await service.GenerateDocumentAsync(
            CreateOrder(), template, requested, isTechnical: false);

        Assert.Equal("documento anterior", await File.ReadAllTextAsync(requested));
        Assert.NotEqual(requested, result.DocumentPath);
        Assert.EndsWith("presupuesto (2).docx", result.DocumentPath);
        Assert.True(File.Exists(result.DocumentPath));
        using var generated = WordprocessingDocument.Open(result.DocumentPath, false);
        Assert.Contains("Cliente & Ñ", generated.MainDocumentPart!.Document!.InnerText);
    }

    [Fact]
    public async Task OpenXmlInformaElMarcadorDeProductosFaltante()
    {
        var template = CreateTemplate("{{CLIENTE}}");
        var service = new OpenXmlDocumentService();

        var error = await Assert.ThrowsAsync<DocumentTemplateValidationException>(() =>
            service.GenerateDocumentAsync(
                CreateOrder(), template, Path.Combine(_root, "sin-marcador.docx"), false));

        Assert.Contains("{{PRODUCTOS_AQUI}}", error.Message);
        Assert.False(File.Exists(Path.Combine(_root, "sin-marcador.docx")));
    }

    [Fact]
    public async Task OpenXmlRechazaUnaPlantillaCorruptaSinDejarSalidaParcial()
    {
        var template = Path.Combine(_root, "corrupta.docx");
        await File.WriteAllTextAsync(template, "no es un paquete OpenXML");
        var output = Path.Combine(_root, "corrupta-salida.docx");

        var error = await Assert.ThrowsAsync<DocumentTemplateValidationException>(() =>
            new OpenXmlDocumentService().GenerateDocumentAsync(CreateOrder(), template, output, false));

        Assert.Contains("dañada", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_root, ".*.tmp.docx"));
    }

    [Fact]
    public async Task OpenXmlOmiteImagenInvalidaYExponeAdvertenciaSegura()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var invalidImage = Path.Combine(_root, "imagen.png");
        await File.WriteAllTextAsync(invalidImage, "contenido inválido");
        var order = CreateOrder();
        order.Items[0].ImagePath = invalidImage;

        var result = await new OpenXmlDocumentService().GenerateDocumentAsync(
            order, template, Path.Combine(_root, "imagen-invalida.docx"), false);

        Assert.True(File.Exists(result.DocumentPath));
        Assert.Contains(result.Warnings, warning => warning.Contains("imagen", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains(invalidImage, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenXmlCanceladoNoPublicaArchivos()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var output = Path.Combine(_root, "cancelado.docx");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenXmlDocumentService().GenerateDocumentAsync(
                CreateOrder(), template, output, false, cancellationToken: cts.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_root, ".*.tmp.docx"));
    }

    [Fact]
    public async Task OpenXmlDeclaraQuePdfNoEstaSoportado()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var result = await new OpenXmlDocumentService().GenerateDocumentAsync(
            CreateOrder(), template, Path.Combine(_root, "sin-pdf.docx"), false, exportPdf: true);

        Assert.Null(result.PdfPath);
        Assert.Contains(result.Warnings, warning => warning.Contains("PDF", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.ChangeExtension(result.DocumentPath, ".pdf")));
    }

    [Fact]
    public async Task OpenXmlSinProductosGeneraUnDocumentoValidoSinMarcadorResidual()
    {
        var template = CreateTemplate("Inicio {{PRODUCTOS_AQUI}} Fin");
        var order = CreateOrder();
        order.Items.Clear();

        var result = await new OpenXmlDocumentService().GenerateDocumentAsync(
            order, template, Path.Combine(_root, "vacio.docx"), false);

        using var generated = WordprocessingDocument.Open(result.DocumentPath, false);
        Assert.DoesNotContain("{{PRODUCTOS_AQUI}}", generated.MainDocumentPart!.Document!.InnerText);
    }

    [Fact]
    public async Task OpenXmlToleraTextoLargoCamposFaltantesYCaracteresEspeciales()
    {
        var template = CreateTemplate("{{CLIENTE}}\n{{COMENTARIOS}}\n{{PRODUCTOS_AQUI}}");
        var order = CreateOrder();
        order.Client = null;
        order.Comments = string.Concat(Enumerable.Repeat("Línea <&> ñ — 漢字\n", 700));
        order.Items[0].DescriptionSnapshot = "[b]Equipo <&> \"especial\" ñ[/b]";

        var result = await new OpenXmlDocumentService().GenerateDocumentAsync(
            order, template, Path.Combine(_root, "texto-largo.docx"), false);

        using var generated = WordprocessingDocument.Open(result.DocumentPath, false);
        Assert.Contains("N/A", generated.MainDocumentPart!.Document!.InnerText);
        Assert.Contains("漢字", generated.MainDocumentPart.Document.InnerText);
        var validationErrors = new OpenXmlValidator().Validate(generated).ToList();
        Assert.True(
            validationErrors.Count == 0,
            string.Join("\n", validationErrors.Take(20).Select(error =>
                $"{error.Description} | {error.Path?.XPath} | {error.Node?.OuterXml}")));
    }

    [Fact]
    public async Task PlantillaConVinculoActivoExternoSeRechaza()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        using (var document = WordprocessingDocument.Open(template, true))
        {
            document.MainDocumentPart!.AddExternalRelationship(
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate",
                new Uri("file:///C:/archivo-externo.dotx"));
        }

        var error = await Assert.ThrowsAsync<DocumentTemplateValidationException>(() =>
            new OpenXmlDocumentService().GenerateDocumentAsync(
                CreateOrder(), template, Path.Combine(_root, "externo.docx"), false));

        Assert.Contains("vínculos externos", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarcadorSoloEnEncabezadoNoPasaLaValidacionDelCuerpo()
    {
        var template = CreateTemplate("{{CLIENTE}}");
        using (var document = WordprocessingDocument.Open(template, true))
        {
            var header = document.MainDocumentPart!.AddNewPart<HeaderPart>();
            header.Header = new W.Header(
                new W.Paragraph(new W.Run(new W.Text("{{PRODUCTOS_AQUI}}"))));
            header.Header.Save();
        }

        var error = await Assert.ThrowsAsync<DocumentTemplateValidationException>(() =>
            new OpenXmlDocumentService().GenerateDocumentAsync(
                CreateOrder(), template, Path.Combine(_root, "header.docx"), false));

        Assert.Contains("{{PRODUCTOS_AQUI}}", error.Message);
    }

    [Fact]
    public async Task JournalNoAutenticadoNoPuedeBorrarUnPdfExistente()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var output = Path.Combine(_root, "recuperado.docx");
        var orphanPdf = Path.ChangeExtension(output, ".pdf");
        await File.WriteAllTextAsync(orphanPdf, "pdf sin commit");
        var journal = Path.Combine(_root, ".alquitel-publish-test.journal");
        await File.WriteAllLinesAsync(journal, new[]
        {
            Path.GetFileName(output),
            Path.GetFileName(orphanPdf),
        });

        var result = await new OpenXmlDocumentService().GenerateDocumentAsync(
            CreateOrder(), template, output, false);

        Assert.EndsWith("recuperado (2).docx", result.DocumentPath);
        Assert.True(File.Exists(orphanPdf));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public async Task JournalProtegidoSoloRecuperaElMismoPdfQueQuedoInterrumpido()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var output = Path.Combine(_root, "transaccion.docx");
        var finalPdf = Path.ChangeExtension(output, ".pdf");
        var stagedPdf = Path.Combine(_root, ".alquitel-prueba.tmp.pdf");
        await File.WriteAllTextAsync(stagedPdf, "pdf exacto de la transacción");

        var safetyType = typeof(OpenXmlDocumentService).Assembly.GetType(
            "Alquitel.Infrastructure.Services.DocumentGenerationSafety")!;
        var writeJournal = safetyType.GetMethod(
            "WritePublicationJournal",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var journalPath = (string)writeJournal.Invoke(
            null, new object[] { _root, output, finalPdf, stagedPdf })!;
        var journalToken = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(journalPath)).AsSpan(0, 8));
        File.Move(stagedPdf, finalPdf);

        try
        {
            var result = await new OpenXmlDocumentService().GenerateDocumentAsync(
                CreateOrder(), template, output, false);

            Assert.Equal(output, result.DocumentPath);
            Assert.False(File.Exists(finalPdf));
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            if (File.Exists(journalPath)) File.Delete(journalPath);
            var quarantine = Path.Combine(
                Alquitel.Infrastructure.AppPaths.AppDataRoot, "DocumentWork", "Quarantine");
            if (Directory.Exists(quarantine))
                foreach (var path in Directory.GetFiles(
                             quarantine, $"publication_{journalToken}_*.bad"))
                    File.Delete(path);
        }
    }

    [Fact]
    public async Task JournalProtegidoPreservaUnPdfReemplazadoDespuesDelCorte()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var output = Path.Combine(_root, "reemplazado.docx");
        var finalPdf = Path.ChangeExtension(output, ".pdf");
        var stagedPdf = Path.Combine(_root, ".alquitel-reemplazo.tmp.pdf");
        await File.WriteAllTextAsync(stagedPdf, "pdf original");

        var safetyType = typeof(OpenXmlDocumentService).Assembly.GetType(
            "Alquitel.Infrastructure.Services.DocumentGenerationSafety")!;
        var writeJournal = safetyType.GetMethod(
            "WritePublicationJournal",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var journalPath = (string)writeJournal.Invoke(
            null, new object[] { _root, output, finalPdf, stagedPdf })!;
        var journalToken = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(journalPath)).AsSpan(0, 8));
        File.Move(stagedPdf, finalPdf);
        await File.WriteAllTextAsync(finalPdf, "pdf reemplazado por el usuario");

        try
        {
            var result = await new OpenXmlDocumentService().GenerateDocumentAsync(
                CreateOrder(), template, output, false);

            Assert.EndsWith("reemplazado (2).docx", result.DocumentPath);
            Assert.Equal("pdf reemplazado por el usuario", await File.ReadAllTextAsync(finalPdf));
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            if (File.Exists(journalPath)) File.Delete(journalPath);
            var quarantine = Path.Combine(
                Alquitel.Infrastructure.AppPaths.AppDataRoot, "DocumentWork", "Quarantine");
            if (Directory.Exists(quarantine))
                foreach (var path in Directory.GetFiles(
                             quarantine, $"publication_{journalToken}_*.bad"))
                    File.Delete(path);
        }
    }

    [Fact]
    public async Task RecuperacionVerificaYEliminaElPdfConElMismoHandleBloqueado()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var output = Path.Combine(_root, "sin-carrera.docx");
        var finalPdf = Path.ChangeExtension(output, ".pdf");
        var stagedPdf = Path.Combine(_root, ".alquitel-sin-carrera.tmp.pdf");
        await File.WriteAllTextAsync(stagedPdf, "pdf exacto de la transacción");

        var safetyType = typeof(OpenXmlDocumentService).Assembly.GetType(
            "Alquitel.Infrastructure.Services.DocumentGenerationSafety")!;
        var writeJournal = safetyType.GetMethod(
            "WritePublicationJournal",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var hookField = safetyType.GetField(
            "ConditionalDeleteTestHook",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var journalPath = (string)writeJournal.Invoke(
            null, new object[] { _root, output, finalPdf, stagedPdf })!;
        var journalToken = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(journalPath)).AsSpan(0, 8));
        File.Move(stagedPdf, finalPdf);
        var replacementWasBlocked = false;
        hookField.SetValue(null, (Action<string>)(path =>
        {
            try
            {
                File.WriteAllText(path, "reemplazo durante la recuperación");
            }
            catch (IOException)
            {
                replacementWasBlocked = true;
            }
        }));

        try
        {
            var result = await new OpenXmlDocumentService().GenerateDocumentAsync(
                CreateOrder(), template, output, false);

            Assert.Equal(output, result.DocumentPath);
            Assert.True(replacementWasBlocked);
            Assert.False(File.Exists(finalPdf));
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            hookField.SetValue(null, null);
            if (File.Exists(journalPath)) File.Delete(journalPath);
            var quarantine = Path.Combine(
                Alquitel.Infrastructure.AppPaths.AppDataRoot, "DocumentWork", "Quarantine");
            if (Directory.Exists(quarantine))
                foreach (var path in Directory.GetFiles(
                             quarantine, $"publication_{journalToken}_*.bad"))
                    File.Delete(path);
        }
    }

    [Fact]
    public async Task GeneracionReportaProgresoYLimpiaTemporalesInterrumpidosAntiguos()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var stale = Path.Combine(_root, ".alquitel-antiguo.tmp.docx");
        await File.WriteAllTextAsync(stale, "PII antigua");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        var stages = new List<DocumentGenerationStage>();
        var progress = new InlineProgress(value => stages.Add(value.Stage));

        await new OpenXmlDocumentService().GenerateDocumentAsync(
            CreateOrder(), template, Path.Combine(_root, "progreso.docx"), false,
            progress: progress);

        Assert.False(File.Exists(stale));
        Assert.Contains(DocumentGenerationStage.Validating, stages);
        Assert.Contains(DocumentGenerationStage.RenderingProducts, stages);
        Assert.Contains(DocumentGenerationStage.Completed, stages);
    }

    [Fact]
    public async Task OrquestacionComEsTesteableSinWordYPublicaSoloAlFinal()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var requested = Path.Combine(_root, "com.docx");
        var session = new FakeWordSession();
        var composer = new FakeComposer();
        var service = new WordDocumentService(
            new FakeSessionFactory(session), composer, TimeSpan.FromSeconds(2));

        var result = await service.GenerateDocumentAsync(
            CreateOrder(), template, requested, false, exportPdf: true);

        Assert.Equal(requested, result.DocumentPath);
        Assert.Equal(Path.ChangeExtension(requested, ".pdf"), result.PdfPath);
        Assert.True(session.Initialized);
        Assert.True(session.Disposed);
        Assert.NotEqual(template, session.OpenedTemplatePath);
        Assert.True(File.Exists(template));
        Assert.False(File.Exists(session.OpenedTemplatePath));
        Assert.True(composer.Called);
        Assert.Equal(ApartmentState.STA, composer.ApartmentState);
        Assert.True(File.Exists(result.DocumentPath));
        Assert.True(File.Exists(result.PdfPath));
    }

    [Fact]
    public async Task TimeoutComCancelaCooperativamenteYNoPublicaSalida()
    {
        var template = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var requested = Path.Combine(_root, "timeout.docx");
        var session = new FakeWordSession();
        var composer = new BlockingComposer();
        var service = new WordDocumentService(
            new FakeSessionFactory(session), composer, TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.GenerateDocumentAsync(CreateOrder(), template, requested, false));

        Assert.True(session.Disposed);
        Assert.False(File.Exists(requested));
        Assert.True(composer.Finished.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TimeoutNoLiberaLaExclusionHastaQueElStaAnteriorTermina()
    {
        var firstTemplate = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var secondTemplate = CreateTemplate("{{PRODUCTOS_AQUI}}");
        var stuckComposer = new UncooperativeComposer();
        var firstService = new WordDocumentService(
            new FakeSessionFactory(new FakeWordSession()),
            stuckComposer,
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => firstService.GenerateDocumentAsync(
            CreateOrder(), firstTemplate, Path.Combine(_root, "primero.docx"), false));

        var secondComposer = new FakeComposer();
        var secondService = new WordDocumentService(
            new FakeSessionFactory(new FakeWordSession()), secondComposer, TimeSpan.FromSeconds(2));
        var secondTask = secondService.GenerateDocumentAsync(
            CreateOrder(), secondTemplate, Path.Combine(_root, "segundo.docx"), false);
        await Task.Delay(150);
        Assert.False(secondComposer.Called);

        stuckComposer.Release();
        var result = await secondTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(secondComposer.Called);
        Assert.True(File.Exists(result.DocumentPath));
    }

    private string CreateTemplate(string text)
    {
        var path = Path.Combine(_root, $"template_{Guid.NewGuid():N}.docx");
        using var document = WordprocessingDocument.Create(
            path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(
            new W.Paragraph(new W.Run(new W.Text(text)))));
        main.Document.Save();
        return path;
    }

    private static Order CreateOrder() => new()
    {
        Id = Guid.NewGuid(),
        BudgetNumber = "123/1",
        AdminName = "Operador",
        CreatedDate = DateTime.UtcNow,
        Client = new Client { CompanyName = "Cliente & Ñ", Cuit = "20-12345678-3" },
        Location = new Location { Name = "Salón <Central>" },
        Items =
        {
            new OrderItem
            {
                Id = Guid.NewGuid(),
                Quantity = 1,
                Dias = 2,
                UnitPrice = 10,
                DescriptionSnapshot = "[b]Pantalla & soporte[/b]",
            },
        },
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeSessionFactory(FakeWordSession session) : IWordComSessionFactory
    {
        public IWordComSession Create() => session;
    }

    private sealed class FakeWordSession : IWordComSession
    {
        public dynamic? WordApp => null;
        public dynamic? Document => new object();
        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }
        public string? OpenedTemplatePath { get; private set; }

        public void Initialize() => Initialized = true;
        public void OpenTemplate(string templatePath) => OpenedTemplatePath = templatePath;
        public void SaveWorkingCopy(string outputPath) => File.WriteAllText(outputPath, "docx listo");
        public void ExportAsPdf(string pdfPath) => File.WriteAllText(pdfPath, "pdf listo");
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeComposer : IWordDocumentComposer
    {
        public bool Called { get; private set; }
        public ApartmentState ApartmentState { get; private set; }

        public void Compose(
            IWordComSession session,
            Order order,
            bool isTechnical,
            ICollection<string> warnings,
            IProgress<DocumentGenerationProgress>? progress,
            CancellationToken cancellationToken)
        {
            Called = true;
            ApartmentState = Thread.CurrentThread.GetApartmentState();
        }
    }

    private sealed class BlockingComposer : IWordDocumentComposer
    {
        public ManualResetEventSlim Finished { get; } = new(false);

        public void Compose(
            IWordComSession session,
            Order order,
            bool isTechnical,
            ICollection<string> warnings,
            IProgress<DocumentGenerationProgress>? progress,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                Finished.Set();
            }
        }
    }

    private sealed class UncooperativeComposer : IWordDocumentComposer
    {
        private readonly ManualResetEventSlim _release = new(false);
        public void Release() => _release.Set();

        public void Compose(
            IWordComSession session,
            Order order,
            bool isTechnical,
            ICollection<string> warnings,
            IProgress<DocumentGenerationProgress>? progress,
            CancellationToken cancellationToken) => _release.Wait();
    }

    private sealed class InlineProgress(Action<DocumentGenerationProgress> report)
        : IProgress<DocumentGenerationProgress>
    {
        public void Report(DocumentGenerationProgress value) => report(value);
    }
}
