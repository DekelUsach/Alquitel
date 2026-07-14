using Alquitel.Core.Helpers;
using Xunit;

namespace Alquitel.Core.Tests
{
    public class MoneyFormatterTests
    {
        [Fact]
        public void Currency_UsaSeparadoresArgentinos()
        {
            var s = MoneyFormatter.Currency(1234567.89m);
            // es-AR: miles con punto, decimales con coma, símbolo $
            Assert.Contains("1.234.567,89", s);
            Assert.Contains("$", s);
            Assert.DoesNotContain("US$", s);
        }

        [Fact]
        public void WholeNumber_SinDecimales_ConMiles()
        {
            Assert.Equal("1.234.567", MoneyFormatter.WholeNumber(1234567m));
            Assert.Equal("0", MoneyFormatter.WholeNumber(0m));
        }

        [Fact]
        public void Currency_EsIndependienteDeLaCulturaDelHilo()
        {
            var original = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("en-US");
                // Aunque el hilo esté en en-US (como un Windows en inglés), el
                // documento debe salir con formato argentino.
                Assert.Contains("1.234,50", MoneyFormatter.Currency(1234.5m));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = original;
            }
        }
    }
}
