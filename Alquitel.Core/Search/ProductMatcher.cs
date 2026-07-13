using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Search
{
    /// <summary>Producto detectado en el texto del cliente, con cantidad y score.</summary>
    public sealed record ProductMatch(Product Product, int Quantity, double Score);

    /// <summary>
    /// Motor de coincidencia de productos del Smart Search: segmentación del texto del
    /// cliente, extracción de cantidades, scoring por tokens + trigramas (coeficiente de
    /// Dice) y filtro de ambigüedad por margen. Lógica pura sin dependencias de UI ni de
    /// base de datos: se construye con un snapshot del catálogo y es 100% testeable.
    /// </summary>
    public sealed class ProductMatcher
    {
        private sealed class ProductCacheEntry
        {
            public HashSet<string> DescriptionTokens { get; init; } = new();
            public HashSet<string> CategoryTokens { get; init; } = new();
            public HashSet<string> DescriptionTrigrams { get; init; } = new();
            public string NormalizedDescription { get; init; } = string.Empty;
        }

        private readonly List<Product> _products;
        private readonly Dictionary<Guid, ProductCacheEntry> _cache;
        private readonly HashSet<string> _stopWords;
        private readonly double _threshold;
        private readonly double _margin;

        public ProductMatcher(IEnumerable<Product> products, IEnumerable<string> stopWords, double threshold, double margin)
        {
            _products = products.ToList();
            _stopWords = new HashSet<string>(stopWords, StringComparer.OrdinalIgnoreCase);
            _threshold = threshold;
            _margin = margin;

            _cache = new Dictionary<Guid, ProductCacheEntry>(_products.Count);
            foreach (var p in _products)
            {
                string nd = NormalizeText(p.Description);
                _cache[p.Id] = new ProductCacheEntry
                {
                    DescriptionTokens = ExtractMeaningfulTokens(nd, _stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    CategoryTokens = ExtractMeaningfulTokens(NormalizeText(p.Category), _stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    DescriptionTrigrams = Trigrams(nd),
                    NormalizedDescription = nd
                };
            }
        }

        /// <summary>
        /// Detecta productos del catálogo en un párrafo libre del cliente. Aplica el
        /// umbral configurado y descarta segmentos ambiguos (diferencia entre el mejor
        /// y el segundo candidato menor al margen) para evitar falsos positivos.
        /// </summary>
        public IReadOnlyList<ProductMatch> FindMatches(string paragraph)
        {
            var aggregated = new Dictionary<Guid, ProductMatch>();

            foreach (var segment in BuildSegments(paragraph))
            {
                int quantity = ExtractQuantity(segment);

                string ns = NormalizeText(segment);
                var st = ExtractMeaningfulTokens(ns, _stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!st.Any()) continue;
                var sTri = Trigrams(ns);

                var ranked = _products
                    .Select(product => new ProductMatch(product, quantity, ScoreSegment(ns, st, sTri, product)))
                    .OrderByDescending(x => x.Score).ToList();
                if (!ranked.Any()) continue;

                var best = ranked[0];
                var second = ranked.Count > 1 ? ranked[1] : null;
                if (best.Score < _threshold) continue;
                if (second != null && Math.Abs(best.Score - second.Score) < _margin) continue;

                if (aggregated.TryGetValue(best.Product.Id, out var existing))
                    aggregated[best.Product.Id] = existing with { Quantity = existing.Quantity + best.Quantity, Score = Math.Max(existing.Score, best.Score) };
                else
                    aggregated[best.Product.Id] = best;
            }
            return aggregated.Values.ToList();
        }

        /// <summary>
        /// Preselección de candidatos para la IA (retrieval): por cada segmento del texto
        /// toma los mejores por score local; si nada da señal cae a similitud de trigramas
        /// contra el texto completo. Mantiene el prompt acotado con catálogos grandes.
        /// </summary>
        public IReadOnlyList<Product> SelectAiCandidates(string customerText,
            int fullCatalogLimit = 60, int candidatesPerSegment = 8, int maxProducts = 80)
        {
            if (_products.Count <= fullCatalogLimit) return _products;

            var candidates = new Dictionary<Guid, (Product Product, double Score)>();

            foreach (var segment in BuildSegments(customerText))
            {
                string ns = NormalizeText(segment);
                var st = ExtractMeaningfulTokens(ns, _stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!st.Any()) continue;
                var sTri = Trigrams(ns);

                var top = _products
                    .Select(p => (Product: p, Score: ScoreSegment(ns, st, sTri, p)))
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .Take(candidatesPerSegment);

                foreach (var (product, score) in top)
                {
                    if (!candidates.TryGetValue(product.Id, out var existing) || score > existing.Score)
                        candidates[product.Id] = (product, score);
                }
            }

            if (candidates.Count == 0)
            {
                // Sin señal por segmentos: mejor esfuerzo con el texto completo.
                var textTrigrams = Trigrams(NormalizeText(customerText));
                return _products
                    .OrderByDescending(p => _cache.TryGetValue(p.Id, out var c)
                        ? DiceCoefficient(textTrigrams, c.DescriptionTrigrams)
                        : 0)
                    .Take(maxProducts)
                    .ToList();
            }

            return candidates.Values
                .OrderByDescending(v => v.Score)
                .Take(maxProducts)
                .Select(v => v.Product)
                .ToList();
        }

        private double ScoreSegment(string normalizedSegment, HashSet<string> segmentTokens, HashSet<string> segmentTrigrams, Product product)
        {
            if (!_cache.TryGetValue(product.Id, out var cache)) return 0;

            var pt = cache.DescriptionTokens;
            var ct = cache.CategoryTokens;

            if (!pt.Any()) return 0;

            int overlap = segmentTokens.Intersect(pt, StringComparer.OrdinalIgnoreCase).Count();
            int catOverlap = segmentTokens.Intersect(ct, StringComparer.OrdinalIgnoreCase).Count();
            double coverage = (double)overlap / pt.Count;
            double precision = (double)overlap / Math.Max(1, segmentTokens.Count);
            double tri = DiceCoefficient(segmentTrigrams, cache.DescriptionTrigrams);

            double score = overlap * 2.7 + catOverlap * 0.8 + coverage * 3.5 + precision * 1.5 + tri * 4.0;

            if (normalizedSegment.Contains(cache.NormalizedDescription, StringComparison.OrdinalIgnoreCase)) score += 3.0;

            return score;
        }

        // ── Piezas estáticas del pipeline (públicas para tests unitarios) ──

        /// <summary>Segmenta por puntuación y luego por la conjunción " y ".</summary>
        public static List<string> BuildSegments(string paragraph)
        {
            var primarySegments = paragraph
                .Split(new[] { '.', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s));
            var expanded = new List<string>();
            foreach (var segment in primarySegments)
            {
                expanded.AddRange(Regex.Split(segment, @"\s+y\s+", RegexOptions.IgnoreCase)
                    .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            return expanded;
        }

        /// <summary>Cantidad pedida en el segmento ("2 pantallas", "x 3", "4 u"); default 1.</summary>
        public static int ExtractQuantity(string segment)
        {
            string raw = RemoveDiacritics(segment).ToLowerInvariant();
            var patterns = new[]
            {
                @"\b(\d{1,3})(?![\.,]\d)\s*(x|u|ud|uds|unidad|unidades)\b",
                @"\b(?:x|por)\s*(\d{1,3})(?![\.,]\d)\b",
                @"\b(\d{1,3})(?![\.,]\d)\s*(?:pantalla|pantallas|notebook|notebooks|camara|camaras|servicio|servicios|traslado|traslados|touch|equipo|equipos)\b"
            };
            foreach (var pattern in patterns)
            {
                var m = Regex.Match(raw, pattern, RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int q) && q > 0) return q;
            }
            return 1;
        }

        public static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string formD = RemoveDiacritics(text);
            var sb = new StringBuilder(formD.Length);
            foreach (char c in formD) sb.Append(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ');
            return Regex.Replace(sb.ToString().ToLowerInvariant(), @"\s+", " ").Trim();
        }

        public static IEnumerable<string> ExtractMeaningfulTokens(string text, HashSet<string> stopWords)
        {
            return Regex.Split(text, @"[^a-z0-9]+")
                .Where(t => t.Length >= 3 && !stopWords.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> Trigrams(string input)
        {
            string text = $"  {input}  ";
            var grams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (text.Length < 3) { grams.Add(text); return grams; }
            for (int i = 0; i <= text.Length - 3; i++) grams.Add(text.Substring(i, 3));
            return grams;
        }

        public static double DiceCoefficient(HashSet<string> a, HashSet<string> b)
        {
            if (!a.Any() || !b.Any()) return 0;
            int intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
            return (2.0 * intersection) / (a.Count + b.Count);
        }

        public static string RemoveDiacritics(string text)
        {
            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
