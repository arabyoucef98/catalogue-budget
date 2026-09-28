using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

public sealed class ProductWorkbookImporterTests
{
    private readonly ProductWorkbookImporter _importer = new();

    [Fact]
    public void ReadsWorkbookLayoutAndCurrentStockWithoutWritingToSource()
    {
        var workbook = CreateWorkbook(
            [
                ["Titre"],
                ["Code article", "Désignation", "Catégorie", "Prix achat", "Prix vente TTC", "Stock initial", "Entrées", "Sorties", "Stock actuel"],
                ["abc-1", "Pull", "Vêtements", "500", "750", "120", "0", "109", "11"]
            ],
            15);
        var originalBytes = workbook.ToArray();

        var preview = _importer.Read(workbook);

        Assert.True(preview.IsValid);
        var product = Assert.Single(preview.Items);
        Assert.Equal("abc-1", product.Code);
        Assert.Equal("Pull", product.Designation);
        Assert.Equal("Vêtements", product.Category);
        Assert.Equal(500m, product.PurchasePrice);
        Assert.Equal(750m, product.SalePrice);
        Assert.Equal(11m, product.StockQuantity);
        Assert.Equal(15m, product.LowStockThreshold);
        Assert.Equal(originalBytes, workbook.ToArray());
    }

    [Fact]
    public void RejectsDuplicateCodesAndNegativePrices()
    {
        using var workbook = CreateWorkbook(
            [
                ["Code article", "Désignation", "Catégorie", "Prix achat", "Prix vente TTC", "Stock initial", "Entrées", "Sorties", "Stock actuel"],
                ["abc-1", "Pull", "Vêtements", "500", "750", "120", "0", "109", "11"],
                ["ABC-1", "Autre", "Vêtements", "500", "750", "120", "0", "100", "20"],
                ["abc-2", "Pantalon", "Vêtements", "100", "-1", "10", "0", "0", "10"]
            ],
            0);

        var preview = _importer.Read(workbook);

        Assert.False(preview.IsValid);
        Assert.Single(preview.Items);
        Assert.Contains(preview.Issues, issue => issue.Field == "Code article");
        Assert.Contains(preview.Issues, issue => issue.Field == "Prix vente TTC");
    }

    [Fact]
    public void RejectsWorkbookWithoutExpectedHeaders()
    {
        using var workbook = CreateWorkbook([["Code", "Name"], ["abc-1", "Pull"]], 0);

        var exception = Assert.Throws<ProductImportException>(() => _importer.Read(workbook));

        Assert.Contains("colonnes", exception.Message);
    }

    [Fact]
    public void ReadsUserWorkbookWithoutChangingItsBytes()
    {
        var path = Environment.GetEnvironmentVariable("CAISSE_TEST_XLSM_PATH");
        if (string.IsNullOrWhiteSpace(path))
            return;

        var before = SHA256.HashData(File.ReadAllBytes(path));
        using var workbook = File.OpenRead(path);
        var preview = _importer.Read(workbook);
        var after = SHA256.HashData(File.ReadAllBytes(path));

        Assert.True(preview.IsValid, string.Join(Environment.NewLine, preview.Issues.Select(issue => $"{issue.RowNumber}: {issue.Field} — {issue.Message}")));
        Assert.Equal(2, preview.Items.Count);
        Assert.Equal(before, after);
    }

    private static MemoryStream CreateWorkbook(string[][] productRows, int threshold)
    {
        var strings = productRows.SelectMany(row => row).Distinct().ToList();
        var stringIndexes = strings.Select((value, index) => (value, index))
            .ToDictionary(item => item.value, item => item.index);
        var workbook = new MemoryStream();
        using (var archive = new ZipArchive(workbook, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>""");
            Write(archive, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Write(archive, "xl/workbook.xml",
                """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Articles" sheetId="1" r:id="rId1"/><sheet name="Paramètres" sheetId="2" r:id="rId2"/></sheets></workbook>""");
            Write(archive, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>""");
            var sharedStrings = string.Concat(strings.Select(value => $"<si><t>{Xml(value)}</t></si>"));
            Write(archive, "xl/sharedStrings.xml",
                $"""<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{strings.Count}" uniqueCount="{strings.Count}">{sharedStrings}</sst>""");
            var rows = new StringBuilder();
            for (var rowIndex = 0; rowIndex < productRows.Length; rowIndex++)
            {
                rows.Append($"<row r=\"{rowIndex + 1}\">");
                for (var columnIndex = 0; columnIndex < productRows[rowIndex].Length; columnIndex++)
                {
                    var column = (char)('A' + columnIndex);
                    var value = productRows[rowIndex][columnIndex];
                    if (decimal.TryParse(value, out var number))
                        rows.Append($"<c r=\"{column}{rowIndex + 1}\"><v>{number}</v></c>");
                    else
                        rows.Append($"<c r=\"{column}{rowIndex + 1}\" t=\"s\"><v>{stringIndexes[value]}</v></c>");
                }
                rows.Append("</row>");
            }
            Write(archive, "xl/worksheets/sheet1.xml",
                $"""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rows}</sheetData></worksheet>""");
            Write(archive, "xl/worksheets/sheet2.xml",
                $"""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="7"><c r="B7"><v>{threshold}</v></c></row></sheetData></worksheet>""");
        }

        workbook.Position = 0;
        return workbook;
    }

    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static void Write(ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }
}
