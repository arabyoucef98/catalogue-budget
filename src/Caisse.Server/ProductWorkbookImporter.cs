using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

public sealed class ProductWorkbookImporter
{
    public const int MaximumWorkbookBytes = 20 * 1024 * 1024;
    private const int MaximumExpandedWorkbookBytes = 100 * 1024 * 1024;
    private const int MaximumProducts = 10_000;
    private const int MaximumWorksheetRows = 50_000;
    private const decimal MaximumPrice = 9_999_999_999_999_999.99m;
    private const decimal MaximumQuantity = 999_999_999_999_999.999m;

    public ProductImportPreview Read(Stream workbook)
    {
        ValidatePackage(workbook);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var threshold = ReadLowStockThreshold(workbook);
        workbook.Position = 0;
        using var reader = ExcelReaderFactory.CreateReader(workbook, new ExcelReaderConfiguration { LeaveOpen = true });
        if (!MoveToSheet(reader, "Articles"))
            throw new ProductImportException("La feuille « Articles » est introuvable.");

        var header = FindHeader(reader);
        var items = new List<ProductImportRow>();
        var issues = new List<ProductImportIssue>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowNumber = header.RowNumber;

        while (reader.Read())
        {
            rowNumber++;
            if (rowNumber - header.RowNumber > MaximumWorksheetRows)
            {
                issues.Add(new ProductImportIssue(rowNumber, "Fichier", $"L’onglet Articles dépasse la limite de {MaximumWorksheetRows} lignes."));
                break;
            }
            if (IsBlank(reader, header))
                continue;
            if (items.Count + issues.Count >= MaximumProducts)
            {
                issues.Add(new ProductImportIssue(rowNumber, "Fichier", $"Le classeur dépasse la limite de {MaximumProducts} articles."));
                break;
            }

            var code = ReadText(reader, header.Code);
            var designation = ReadText(reader, header.Designation);
            var category = ReadText(reader, header.Category);
            if (string.IsNullOrWhiteSpace(code))
            {
                issues.Add(new ProductImportIssue(rowNumber, "Code article", "Le code article est obligatoire."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(designation))
            {
                issues.Add(new ProductImportIssue(rowNumber, "Désignation", "La désignation est obligatoire."));
                continue;
            }
            if (code.Length > 80 || designation.Length > 240 || category?.Length > 120)
            {
                issues.Add(new ProductImportIssue(rowNumber, "Article", "Les longueurs maximales sont : code 80, désignation 240, catégorie 120 caractères."));
                continue;
            }
            if (!seenCodes.Add(code))
            {
                issues.Add(new ProductImportIssue(rowNumber, "Code article", $"Le code « {code} » apparaît plusieurs fois."));
                continue;
            }

            if (!TryReadDecimal(reader, header.PurchasePrice, out var purchasePrice) || purchasePrice < 0 || purchasePrice > MaximumPrice || HasMoreThanDecimals(purchasePrice, 2))
            {
                issues.Add(new ProductImportIssue(rowNumber, "Prix achat", "Le prix doit être positif ou nul et comporter au plus deux décimales."));
                continue;
            }
            if (!TryReadDecimal(reader, header.SalePrice, out var salePrice) || salePrice < 0 || salePrice > MaximumPrice || HasMoreThanDecimals(salePrice, 2))
            {
                issues.Add(new ProductImportIssue(rowNumber, "Prix vente TTC", "Le prix doit être positif ou nul et comporter au plus deux décimales."));
                continue;
            }
            if (!TryReadDecimal(reader, header.Stock, out var stock) || stock < 0 || stock > MaximumQuantity || HasMoreThanDecimals(stock, 3))
            {
                issues.Add(new ProductImportIssue(rowNumber, "Stock actuel", "Le stock doit être positif ou nul et comporter au plus trois décimales."));
                continue;
            }

            items.Add(new ProductImportRow(code, designation, category, purchasePrice, salePrice, stock, threshold, rowNumber));
        }

        if (items.Count == 0 && issues.Count == 0)
            issues.Add(new ProductImportIssue(header.RowNumber, "Articles", "Aucun article n’a été trouvé après l’en-tête."));

        return new ProductImportPreview(items, issues);
    }

    private static void ValidatePackage(Stream stream)
    {
        if (!stream.CanSeek)
            throw new ProductImportException("Le fichier importé ne peut pas être lu de manière sécurisée.");
        if (stream.Length is 0 or > MaximumWorkbookBytes)
            throw new ProductImportException($"Le fichier doit contenir entre 1 octet et {MaximumWorkbookBytes / (1024 * 1024)} Mo.");

        stream.Position = 0;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 10_000 || archive.Entries.Sum(entry => entry.Length) > MaximumExpandedWorkbookBytes)
                throw new ProductImportException("Le classeur dépasse les limites de sécurité autorisées.");
            if (!archive.Entries.Any(entry => entry.FullName.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase)))
                throw new ProductImportException("Le fichier n’est pas un classeur Excel valide.");
        }
        catch (InvalidDataException ex)
        {
            throw new ProductImportException("Le fichier n’est pas un classeur Excel XLSM valide.", ex);
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static decimal ReadLowStockThreshold(Stream workbook)
    {
        try
        {
            workbook.Position = 0;
            using var reader = ExcelReaderFactory.CreateReader(workbook, new ExcelReaderConfiguration { LeaveOpen = true });
            if (!MoveToSheet(reader, "Paramètres"))
                return 0;

            for (var row = 0; row <= 6; row++)
            {
                if (!reader.Read())
                    throw new ProductImportException("La feuille « Paramètres » ne contient pas le seuil de stock.");
            }

            var thresholdValue = reader.GetValue(1);
            if (thresholdValue is null || thresholdValue is string text && string.IsNullOrWhiteSpace(text))
                return 0;
            if (!TryReadDecimal(reader, 1, out var threshold) || threshold < 0
                || threshold > MaximumQuantity || HasMoreThanDecimals(threshold, 3))
                throw new ProductImportException("Le seuil de stock de la feuille « Paramètres » doit être positif ou nul, avec au plus trois décimales.");
            return threshold;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            throw new ProductImportException("Lecture de la feuille « Paramètres » impossible.", ex);
        }
    }

    private static bool MoveToSheet(IExcelDataReader reader, string name)
    {
        do
        {
            if (Normalize(reader.Name) == Normalize(name))
                return true;
        } while (reader.NextResult());
        return false;
    }

    private static (int RowNumber, int Code, int Designation, int Category, int PurchasePrice, int SalePrice, int Stock) FindHeader(IExcelDataReader reader)
    {
        for (var rowNumber = 1; rowNumber <= 30 && reader.Read(); rowNumber++)
        {
            var columns = new Dictionary<string, int>();
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var value = Normalize(ReadText(reader, column));
                if (value.Length > 0)
                    columns.TryAdd(value, column);
            }

            if (TryFind(columns, out var code, "codearticle", "code")
                && TryFind(columns, out var designation, "designation", "libelle")
                && TryFind(columns, out var category, "categorie", "category")
                && TryFind(columns, out var purchasePrice, "prixachat", "prixdachat")
                && TryFind(columns, out var salePrice, "prixventettc", "prixvente")
                && TryFind(columns, out var stock, "stockactuel", "stockquantity"))
            {
                return (rowNumber, code, designation, category, purchasePrice, salePrice, stock);
            }
        }

        throw new ProductImportException("Les colonnes Code article, Désignation, Catégorie, Prix achat, Prix vente TTC et Stock actuel sont requises dans la feuille « Articles ».");
    }

    private static bool TryFind(Dictionary<string, int> columns, out int index, params string[] names)
    {
        foreach (var name in names)
        {
            if (columns.TryGetValue(name, out index))
                return true;
        }

        index = -1;
        return false;
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var withoutDiacritics = new string(decomposed.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(withoutDiacritics, @"[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();
    }

    private static string? ReadText(IExcelDataReader reader, int column)
    {
        if (column < 0 || column >= reader.FieldCount || reader.GetValue(column) is not { } value)
            return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
    }

    private static bool TryReadDecimal(IExcelDataReader reader, int column, out decimal result)
    {
        var value = column >= 0 && column < reader.FieldCount ? reader.GetValue(column) : null;
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            result = 0;
            return false;
        }

        if (value is string raw)
        {
            raw = raw.Replace('\u00a0', ' ').Replace('\u202f', ' ').Trim();
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.GetCultureInfo("fr-FR"), out result)
                || decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
        }

        try
        {
            result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static bool HasMoreThanDecimals(decimal value, int decimals) => value != decimal.Round(value, decimals);

    private static bool IsBlank(
        IExcelDataReader reader,
        (int RowNumber, int Code, int Designation, int Category, int PurchasePrice, int SalePrice, int Stock) header)
    {
        foreach (var column in new[] { header.Code, header.Designation, header.Category, header.PurchasePrice, header.SalePrice, header.Stock })
        {
            if (!string.IsNullOrWhiteSpace(ReadText(reader, column)))
                return false;
        }
        return true;
    }
}

public sealed record ProductImportRow(string Code, string Designation, string? Category, decimal PurchasePrice, decimal SalePrice, decimal StockQuantity, decimal LowStockThreshold, int RowNumber);
public sealed record ProductImportIssue(int RowNumber, string Field, string Message);
public sealed record ProductImportPreview(IReadOnlyList<ProductImportRow> Items, IReadOnlyList<ProductImportIssue> Issues)
{
    public bool IsValid => Items.Count > 0 && Issues.Count == 0;
}

public sealed class ProductImportException(string message, Exception? innerException = null) : Exception(message, innerException);
