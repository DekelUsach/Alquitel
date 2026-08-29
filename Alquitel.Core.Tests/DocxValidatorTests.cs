using System.IO.Compression;
using System.Text;
using Alquitel.Core.Security;

namespace Alquitel.Core.Tests;

public class DocxValidatorTests
{
    /// <summary>Arma en memoria el esqueleto mínimo de un .docx real.</summary>
    private static byte[] DocxMinimo()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                w.Write("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                w.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"x\"><w:body/></w:document>");
            // Relleno sin comprimir para superar el mínimo de tamaño (un .docx real
            // ronda las decenas de KB; el mínimo del validador son 512 bytes).
            using (var w = new StreamWriter(zip.CreateEntry("_rels/.rels", CompressionLevel.NoCompression).Open()))
                w.Write(new string('x', 600));
        }
        return ms.ToArray();
    }

    [Fact]
    public void DocxValido_SeAcepta()
    {
        var bytes = DocxMinimo();
        Assert.True(DocxValidator.IsValidDocx(bytes));
        Assert.Null(DocxValidator.Describe(bytes));
    }

    [Fact]
    public void PaginaDeErrorHtml_SeRechaza()
    {
        // El caso real: el gateway responde 200 con un HTML de error en vez del .docx.
        // Sin esta validación, ese HTML iba al cache y Word lo abría como plantilla.
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>" + new string('x', 800) + "</body></html>");
        Assert.False(DocxValidator.IsValidDocx(html));
        Assert.Contains("firma ZIP", DocxValidator.Describe(html));
    }

    [Fact]
    public void ZipQueNoEsDocx_SeRechaza()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        // Sin compresión: con deflate el relleno se achica a ~150 bytes y el archivo
        // caería por el filtro de tamaño mínimo antes de llegar al de estructura.
        using (var w = new StreamWriter(zip.CreateEntry("cualquier-cosa.txt", CompressionLevel.NoCompression).Open()))
            w.Write(new string('x', 800));

        var bytes = ms.ToArray();
        Assert.False(DocxValidator.IsValidDocx(bytes));
        Assert.Contains("estructura", DocxValidator.Describe(bytes));
    }

    [Fact]
    public void DescargaCortada_SeRechaza()
    {
        var truncado = DocxMinimo()[..100];
        Assert.False(DocxValidator.IsValidDocx(truncado));
        Assert.NotNull(DocxValidator.Describe(truncado));
    }

    [Theory]
    [InlineData(null)]
    public void Nulo_SeRechaza(byte[]? bytes)
    {
        Assert.False(DocxValidator.IsValidDocx(bytes));
        Assert.NotNull(DocxValidator.Describe(bytes));
    }

    [Fact]
    public void Vacio_SeRechaza()
    {
        Assert.False(DocxValidator.IsValidDocx(Array.Empty<byte>()));
        Assert.Contains("vacía", DocxValidator.Describe(Array.Empty<byte>()));
    }

    [Fact]
    public void ArchivoDemasiadoGrande_SeRechaza()
    {
        // Tope de memoria: una respuesta gigante no debe cargarse entera y guardarse.
        var enorme = new byte[DocxValidator.MaxSizeBytes + 1];
        enorme[0] = 0x50; enorme[1] = 0x4B; enorme[2] = 0x03; enorme[3] = 0x04;
        Assert.False(DocxValidator.IsValidDocx(enorme));
        Assert.Contains("tamaño máximo", DocxValidator.Describe(enorme));
    }
}
