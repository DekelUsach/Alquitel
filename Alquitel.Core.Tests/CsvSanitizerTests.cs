using Alquitel.Core.Helpers;
using System.Text;
using Xunit;

namespace Alquitel.Core.Tests
{
    public class CsvSanitizerTests
    {
        [Fact]
        public void SanitizeField_NullOrEmpty_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, CsvSanitizer.SanitizeField(null));
            Assert.Equal(string.Empty, CsvSanitizer.SanitizeField(string.Empty));
        }

        [Fact]
        public void SanitizeField_NormalText_ReturnsSameText()
        {
            Assert.Equal("Grupo Alquitel", CsvSanitizer.SanitizeField("Grupo Alquitel"));
            Assert.Equal("Microfono Shure SM58", CsvSanitizer.SanitizeField("Microfono Shure SM58"));
            Assert.Equal("12345678", CsvSanitizer.SanitizeField("12345678"));
        }

        [Fact]
        public void SanitizeField_ContainsSeparator_QuotesField()
        {
            var result = CsvSanitizer.SanitizeField("Eventos; Congresos");
            Assert.Equal("\"Eventos; Congresos\"", result);
        }

        [Fact]
        public void SanitizeField_ContainsQuotes_EscapesQuotesAndWraps()
        {
            var result = CsvSanitizer.SanitizeField("Pantalla LED 55\" Samsung");
            Assert.Equal("\"Pantalla LED 55\"\" Samsung\"", result);
        }

        [Fact]
        public void SanitizeField_ContainsNewlines_QuotesField()
        {
            var result = CsvSanitizer.SanitizeField("Nota linea 1\nNota linea 2");
            Assert.Equal("\"Nota linea 1\nNota linea 2\"", result);
        }

        [Theory]
        [InlineData("=cmd|'/C calc'!A0")]
        [InlineData("=SUM(A1:A10)")]
        [InlineData("=1+1")]
        public void SanitizeField_FormulaStartingWithEquals_PrefixesSingleQuote(string formula)
        {
            var result = CsvSanitizer.SanitizeField(formula);
            Assert.StartsWith("\"'", result);
            Assert.Contains(formula.Replace("\"", "\"\""), result);
        }

        [Theory]
        [InlineData("@SUM(A1:A10)")]
        [InlineData("@HYPERLINK(\"http://malicious.com\")")]
        public void SanitizeField_FormulaStartingWithAt_PrefixesSingleQuote(string formula)
        {
            var result = CsvSanitizer.SanitizeField(formula);
            Assert.StartsWith("\"'", result);
            Assert.Contains("@", result);
        }

        [Theory]
        [InlineData("\t=1+2")]
        [InlineData("\r=1+2")]
        public void SanitizeField_FormulaStartingWithTabOrReturn_PrefixesSingleQuote(string formula)
        {
            var result = CsvSanitizer.SanitizeField(formula);
            Assert.StartsWith("\"'", result);
        }

        [Theory]
        [InlineData("+cmd|'/C calc'!A0")]
        [InlineData("-2+3+cmd|'/C calc'!A0")]
        [InlineData("+SUM(A1:A5)")]
        public void SanitizeField_FormulaStartingWithPlusOrMinus_PrefixesSingleQuote(string formula)
        {
            var result = CsvSanitizer.SanitizeField(formula);
            Assert.StartsWith("\"'", result);
        }

        [Theory]
        [InlineData("-125")]
        [InlineData("+50")]
        [InlineData("-1250.75")]
        [InlineData("+0.5")]
        public void SanitizeField_LegitimateNumber_NotTreatedAsFormula(string number)
        {
            var result = CsvSanitizer.SanitizeField(number);
            Assert.Equal(number, result);
        }

        [Fact]
        public void FormatRow_JoinsWithSeparator()
        {
            var fields = new[] { "Empresa", "=1+2", "Observaciones; técnicas", "5000" };
            var row = CsvSanitizer.FormatRow(fields, ';');

            Assert.Equal("Empresa;\"'=1+2\";\"Observaciones; técnicas\";5000", row);
        }

        [Fact]
        public void BuildCsv_GeneratesHeaderAndData()
        {
            var headers = new[] { "ID", "Nombre", "Formula" };
            var rows = new[]
            {
                new[] { "1", "Alquitel", "=cmd" },
                new[] { "2", "Cliente 2", "Texto normal" }
            };

            var csv = CsvSanitizer.BuildCsv(headers, rows, ';');
            Assert.Contains("ID;Nombre;Formula", csv);
            Assert.Contains("1;Alquitel;\"'=cmd\"", csv);
            Assert.Contains("2;Cliente 2;Texto normal", csv);
        }

        [Fact]
        public void ExcelUtf8Encoding_EmitsBom()
        {
            var encoding = CsvSanitizer.ExcelUtf8Encoding;
            var preamble = encoding.GetPreamble();

            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, preamble);
        }
    }
}
