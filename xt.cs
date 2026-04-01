using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

class Program {
    static void Main() {
        using var zip = ZipFile.OpenRead(@"C:\Alquitel\1_PRESUPUESTOS\template.docx");
        var entry = zip.GetEntry("word/document.xml");
        using var stream = new StreamReader(entry.Open());
        string xml = stream.ReadToEnd();
        var matches = Regex.Matches(xml, @"(?<=<w:t>)[^<]*(?:\{\{|\[\[|<<).*?(?:\}\}|\]\]|>>)[^<]*|(?<=<w:t>)[^<]*(?:CLIENTE|EMPRESA|CUIT|FECHA|NUMERO|OT|OF|PRESUPUESTO|LUGAR|AV EVENTOS)[^<]*", RegexOptions.IgnoreCase);
        using var w = new StreamWriter(@"C:\Alquitel\found.txt");
        foreach(Match m in matches) w.WriteLine(m.Value);
    }
}
