// 用途：生成完全虚构的 Office/PDF 测试文件；不依赖 Office，不读取公司资料。
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace PrivateKnowledge.Tests;

/// <summary>供解析测试和自动更新测试共用的小文件工厂；生成值均是手写虚构样例。</summary>
internal static class DocumentFixtures
{
    /// <summary>生成带中文标题、正文和简单表格的 DOCX。</summary>
    /// <param name="instruction">测试需要识别或替换的操作说明。</param>
    /// <param name="merged">true 构造本版应明确拒绝的合并表格。</param>
    /// <returns>可直接写入临时目录的 DOCX 字节。</returns>
    public static byte[] Word(string instruction = "设备连接成功后先启用采集通道。", bool merged = false)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(new W.Style(new W.StyleName { Val = "Heading 1" },
                new W.StyleParagraphProperties(new W.OutlineLevel { Val = 0 })) { StyleId = "CustomTitle", Type = W.StyleValues.Paragraph });
            var title = new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "CustomTitle" }),
                new W.Run(new W.Text("温度采集操作指南")));
            var table = new W.Table(new W.TableRow(Cell("项目"), Cell("操作")),
                new W.TableRow(Cell("温度监控"), Cell(instruction)));
            if (merged) table.Descendants<W.TableCell>().First().PrependChild(new W.TableCellProperties(new W.GridSpan { Val = 2 }));
            main.Document = new W.Document(new W.Body(title, new W.Paragraph(new W.Run(new W.Text(instruction))), table));
            main.Document.Save();
        }
        return stream.ToArray();

        // 每个表格单元格都保存普通文字，避免测试把标题和数据列对错。
        static W.TableCell Cell(string value) => new(new W.Paragraph(new W.Run(new W.Text(value))));
    }

    /// <summary>生成带稀疏列、共享文字、日期、补零编号和公式缓存的 XLSX。</summary>
    /// <param name="instruction">C 列操作说明，供替换更新测试使用。</param>
    /// <param name="merged">是否制造合并单元格。</param>
    /// <param name="missingFormula">是否去掉公式缓存，验证不能自行猜测公式结果。</param>
    /// <param name="date1904">是否使用另一种日期系统，验证不能错移 1462 天。</param>
    /// <returns>真实 XLSX 文件字节。</returns>
    public static byte[] Spreadsheet(string instruction = "设备连接成功后先启用采集通道。",
        bool merged = false, bool missingFormula = false, bool date1904 = false)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var book = document.AddWorkbookPart();
            book.Workbook = new S.Workbook(new S.WorkbookProperties { Date1904 = date1904 });
            var shared = book.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text("项目")), new S.SharedStringItem(new S.Text("温度监控")));
            var styles = book.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(
                new S.NumberingFormats(new S.NumberingFormat { NumberFormatId = 164, FormatCode = "000000" }),
                new S.CellFormats(new S.CellFormat(), new S.CellFormat { NumberFormatId = 14 }, new S.CellFormat { NumberFormatId = 164 }));
            var part = book.AddNewPart<WorksheetPart>();
            var formula = new S.Cell { CellReference = "F9", CellFormula = new S.CellFormula("1+2") };
            if (!missingFormula) formula.CellValue = new S.CellValue("3");
            var header = new S.Row(Shared("A3", 0), Inline("C3", "操作"), Inline("D3", "日期"), Inline("E3", "编号"), Inline("F3", "数量")) { RowIndex = 3 };
            var row = new S.Row(Shared("A9", 1), Inline("C9", instruction),
                new S.Cell { CellReference = "D9", CellValue = new S.CellValue(date1904 ? "0" : "45000"), StyleIndex = 1 },
                new S.Cell { CellReference = "E9", CellValue = new S.CellValue("42"), StyleIndex = 2 }, formula) { RowIndex = 9 };
            part.Worksheet = new S.Worksheet(new S.SheetData(header, row));
            if (merged) part.Worksheet.Append(new S.MergeCells(new S.MergeCell { Reference = "A3:B3" }));
            var other = book.AddNewPart<WorksheetPart>();
            other.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(Inline("A1", "备注")) { RowIndex = 1 }, new S.Row(Inline("A2", "另一张表的备注。")) { RowIndex = 2 }));
            book.Workbook.Append(new S.Sheets(new S.Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "操作规则" },
                new S.Sheet { Id = book.GetIdOfPart(other), SheetId = 2, Name = "补充说明" }));
            book.Workbook.Save();
        }
        return stream.ToArray();

        // 共享文字和内联文字两种常见保存方式都必须能读到。
        static S.Cell Shared(string address, int index) => new() { CellReference = address, DataType = S.CellValues.SharedString, CellValue = new S.CellValue(index.ToString()) };
        static S.Cell Inline(string address, string text) => new() { CellReference = address, DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) };
    }

    /// <summary>在虚构工作簿中单独设置 D9 的格式，验证日期、时间和时长不会当成普通数字。</summary>
    /// <param name="value">Excel 原始数字，如 0.5 表示半天。</param>
    /// <param name="format">自定义显示格式，例如 h:mm。</param>
    /// <returns>除 D9 外与常规样例一致的 XLSX。</returns>
    public static byte[] SpreadsheetWithNumberFormat(string value, string format)
    {
        using var stream = new MemoryStream();
        stream.Write(Spreadsheet());
        stream.Position = 0;
        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            var book = document.WorkbookPart!;
            var styles = book.WorkbookStylesPart!.Stylesheet!;
            styles.NumberingFormats!.Append(new S.NumberingFormat { NumberFormatId = 165, FormatCode = format });
            styles.CellFormats!.Append(new S.CellFormat { NumberFormatId = 165 });
            var cell = book.WorksheetParts.SelectMany(p => p.Worksheet!.Descendants<S.Cell>()).Single(c => c.CellReference?.Value == "D9");
            cell.CellValue = new S.CellValue(value);
            cell.StyleIndex = 3;
        }
        return stream.ToArray();
    }

    /// <summary>生成三页文字 PDF，包含完全重复页眉页脚和不同正文，验证页码和清理。</summary>
    /// <param name="instruction">英文正文，不依赖操作系统字体，便于其他电脑运行测试。</param>
    /// <returns>带可提取文字的 PDF 字节。</returns>
    public static byte[] Pdf(string instruction = "Enable a channel before collecting data.")
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 1; i <= 3; i++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText("PrivateKnowledge manual", 12, new PdfPoint(40, 810), font);
            page.AddText(instruction + " " + i, 12, new PdfPoint(40, 740), font);
            page.AddText("Training copy", 10, new PdfPoint(40, 30), font);
        }
        return builder.Build();
    }

    /// <summary>构造带正常文字页和扫描图片页的混合 PDF，避免只检测整本完全无文字的情况。</summary>
    /// <returns>第一页有文字，第二页只有一张小图片的 PDF。</returns>
    public static byte[] MixedImagePdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(595, 842).AddText("A readable first page.", 12, new PdfPoint(40, 740), font);
        // 手写的 1×1 PNG 测试像素，只用于制造图片页；没有真实图片或个人信息。
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        builder.AddPage(595, 842).AddPng(png, new PdfRectangle(40, 40, 400, 700));
        return builder.Build();
    }
}
