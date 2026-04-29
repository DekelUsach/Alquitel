using Alquitel.Core.Entities;
using System.Threading.Tasks;

namespace Alquitel.Core.Interfaces
{
    public interface IDocumentService
    {
        /// <summary>
        /// Generates a document from a template using bookmarks.
        /// </summary>
        /// <param name="order">The order data to interpolate.</param>
        /// <param name="templatePath">Path to the Word template.</param>
        /// <param name="outputPath">Where to save the result.</param>
        /// <param name="isTechnical">If true, omits monetary values (OT).</param>
        /// <returns>Task for completion.</returns>
        Task GenerateDocumentAsync(Order order, string templatePath, string outputPath, bool isTechnical, bool exportPdf = false);
    }
}
