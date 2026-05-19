using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VehiclePortal.Services
{
    /// <summary>
    /// Result of running OCR on a document.
    /// </summary>
    public class OcrResult
    {
        public bool Success { get; set; }
        public string RawText { get; set; } = string.Empty;
        public string? ExtractedVin { get; set; }
        public string? ExtractedMake { get; set; }
        public string? ExtractedModel { get; set; }
        public int? ExtractedYear { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Abstraction over OCR engine.
    /// Local: Tesseract CLI. Production: AWS Textract.
    /// Swap in Program.cs with one line change.
    /// </summary>
    public interface IOcrService
    {
        Task<OcrResult> ProcessAsync(
            Stream fileStream,
            string contentType,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Tesseract OCR implementation using the CLI directly.
    /// Bypasses the .NET wrapper library loading issues entirely.
    /// Calls the system `tesseract` binary which is installed in the container.
    /// </summary>
    public class TesseractOcrService : IOcrService
    {
        private readonly ILogger<TesseractOcrService> _logger;

        // VIN pattern: 17 alphanumeric chars, no I O Q
        private static readonly Regex VinRegex = new(
            @"\b([A-HJ-NPR-Z0-9]{17})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Year pattern: 4 digit year 1900–2099
        private static readonly Regex YearRegex = new(
            @"\b(19[0-9]{2}|20[0-9]{2})\b",
            RegexOptions.Compiled);

        // Common vehicle makes
        private static readonly string[] KnownMakes =
        [
            "Toyota", "Honda", "Ford", "Chevrolet", "Chevy", "BMW", "Mercedes",
            "Audi", "Volkswagen", "VW", "Nissan", "Hyundai", "Kia", "Mazda",
            "Subaru", "Dodge", "Jeep", "Ram", "GMC", "Buick", "Cadillac",
            "Lincoln", "Acura", "Lexus", "Infiniti", "Volvo", "Porsche",
            "Tesla", "Rivian", "Lucid", "Genesis", "Mitsubishi", "Chrysler"
        ];

        public TesseractOcrService(ILogger<TesseractOcrService> logger)
        {
            _logger = logger;
        }

        public async Task<OcrResult> ProcessAsync(
            Stream fileStream,
            string contentType,
            CancellationToken ct = default)
        {
            try
            {
                // Read file into memory
                byte[] fileBytes;
                await using (var ms = new MemoryStream())
                {
                    await fileStream.CopyToAsync(ms, ct);
                    fileBytes = ms.ToArray();
                }

                string rawText;

                if (contentType == "application/pdf")
                    rawText = await RunTesseractAsync(fileBytes, "pdf", ct);
                else
                    rawText = await RunTesseractAsync(fileBytes, "png", ct);

                if (string.IsNullOrWhiteSpace(rawText))
                {
                    return new OcrResult
                    {
                        Success = false,
                        Error = "No text could be extracted from the document."
                    };
                }

                _logger.LogInformation(
                    "[OCR] Extracted {Length} chars of text", rawText.Length);

                var vin = ExtractVin(rawText);
                var year = ExtractYear(rawText);
                var make = ExtractMake(rawText);

                _logger.LogInformation(
                    "[OCR] Parsed — VIN: {Vin} Make: {Make} Year: {Year}",
                    vin ?? "not found", make ?? "not found",
                    year?.ToString() ?? "not found");

                return new OcrResult
                {
                    Success = true,
                    RawText = rawText,
                    ExtractedVin = vin,
                    ExtractedMake = make,
                    ExtractedYear = year
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OCR] Failed to process document");
                return new OcrResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        // ── Tesseract CLI runner ──────────────────────────────────────────────
        private async Task<string> RunTesseractAsync(
            byte[] fileBytes,
            string extension,
            CancellationToken ct)
        {
            // Write file to temp location
            var tempInput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{extension}");
            var tempOutput = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            try
            {
                await File.WriteAllBytesAsync(tempInput, fileBytes, ct);

                var psi = new ProcessStartInfo
                {
                    FileName = "tesseract",
                    Arguments = $"\"{tempInput}\" \"{tempOutput}\" -l eng",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "[OCR] Tesseract exited with code {Code}: {Error}",
                        process.ExitCode, stderr);
                    return string.Empty;
                }

                // Tesseract appends .txt to output path
                var outputFile = tempOutput + ".txt";
                if (!File.Exists(outputFile))
                {
                    _logger.LogWarning("[OCR] Output file not found: {Path}", outputFile);
                    return string.Empty;
                }

                var text = await File.ReadAllTextAsync(outputFile, ct);
                _logger.LogDebug("[OCR] Raw text: {Text}", text);
                return text;
            }
            finally
            {
                // Clean up temp files
                if (File.Exists(tempInput)) File.Delete(tempInput);
                if (File.Exists(tempOutput)) File.Delete(tempOutput);
                if (File.Exists(tempOutput + ".txt")) File.Delete(tempOutput + ".txt");
            }
        }

        // ── VIN extraction ────────────────────────────────────────────────────
        private static string? ExtractVin(string text)
        {
            var matches = VinRegex.Matches(text.ToUpper());
            foreach (Match match in matches)
            {
                var candidate = match.Value.ToUpper();
                if (IsValidVin(candidate))
                    return candidate;
            }
            return null;
        }

        private static bool IsValidVin(string vin)
        {
            if (vin.Length != 17) return false;
            return vin.All(c =>
                char.IsLetterOrDigit(c) &&
                c != 'I' && c != 'O' && c != 'Q');
        }

        // ── Year extraction ───────────────────────────────────────────────────
        private static int? ExtractYear(string text)
        {
            var matches = YearRegex.Matches(text);
            if (matches.Count == 0) return null;

            return matches
                .GroupBy(m => int.Parse(m.Value))
                .OrderByDescending(g => g.Count())
                .First()
                .Key;
        }

        // ── Make extraction ───────────────────────────────────────────────────
        private static string? ExtractMake(string text)
        {
            var upperText = text.ToUpper();
            foreach (var make in KnownMakes)
            {
                if (upperText.Contains(make.ToUpper()))
                    return make;
            }
            return null;
        }
    }
}