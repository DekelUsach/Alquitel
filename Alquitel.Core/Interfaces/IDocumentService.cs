using Alquitel.Core.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Alquitel.Core.Interfaces
{
    public enum DocumentGenerationStage
    {
        Validating,
        Preparing,
        ReplacingFields,
        RenderingProducts,
        Saving,
        ExportingPdf,
        Completed,
    }

    public sealed record DocumentGenerationProgress(
        DocumentGenerationStage Stage,
        int Percent,
        string Message);

    public sealed record DocumentGenerationResult(
        string DocumentPath,
        string? PdfPath,
        IReadOnlyList<string> Warnings);

    public interface IDocumentService
    {
        /// <summary>
        /// Generates a document from a template using bookmarks.
        /// </summary>
        /// <param name="order">The order data to interpolate.</param>
        /// <param name="templatePath">Path to the Word template.</param>
        /// <param name="outputPath">Where to save the result.</param>
        /// <param name="isTechnical">If true, omits monetary values (OT).</param>
        /// <returns>The paths actually published. Existing files are never overwritten.</returns>
        Task<DocumentGenerationResult> GenerateDocumentAsync(
            Order order,
            string templatePath,
            string outputPath,
            bool isTechnical,
            bool exportPdf = false,
            IProgress<DocumentGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
