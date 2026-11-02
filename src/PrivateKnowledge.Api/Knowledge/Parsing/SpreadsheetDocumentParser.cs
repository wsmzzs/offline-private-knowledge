// 用途：把常规 XLSX 的每一行转成“列名：值”，保留工作表和行号，不运行公式。
using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PrivateKnowledge.Api.Configuration;

namespace PrivateKnowledge.Api.Knowledge.Parsing;

/// <summary>Excel 解析器；每张表使用首个非空行作为单行表头，复杂合并表格明确拒绝。</summary>
internal static class SpreadsheetDocumentParser
{
    /// <summary>遍历工作表和实际存在的行，不把空白单元格造成的列偏移带入知识库。</summary>
    /// <param name="bytes">XLSX 原始字节。</param>
    /// <param name="result">按行保存正文与出处的集合。</param>
    /// <param name="options">文件展开体积限制。</param>
    /// <param name="token">关闭服务时在工作表和行边界结束。</param>
    public static void Parse(byte[] bytes, ParsedDocument result, KnowledgeOptions options, CancellationToken token)
    {
        ParsedDocument.CheckOfficePackage(bytes, options, token);
        using var stream = new MemoryStream(bytes, false);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("XLSX 缺少工作簿。");
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(item => item.InnerText).ToArray() ?? [];
        var styles = workbook.WorkbookStylesPart?.Stylesheet;
        var book = workbook.Workbook ?? throw new InvalidDataException("XLSX 缺少工作簿定义。");
        var date1904 = book.WorkbookProperties?.Date1904?.Value ?? false;
        foreach (var sheet in book.Sheets?.Elements<Sheet>() ?? [])
        {
            token.ThrowIfCancellationRequested();
            var sheetName = sheet.Name?.Value ?? "未命名工作表";
            if (sheet.Id?.Value is not string relationship || workbook.GetPartById(relationship) is not WorksheetPart part)
                throw new InvalidDataException($"工作表“{sheetName}”不是普通数据工作表，请单独另存为 XLSX。");
            var worksheet = part.Worksheet ?? throw new InvalidDataException($"工作表“{sheetName}”缺少正文。");
            if (worksheet.Descendants<MergeCell>().Any())
                throw new InvalidDataException($"工作表“{sheetName}”包含合并单元格，请取消合并并整理成单行表头后重试。");
            var data = worksheet.GetFirstChild<SheetData>();
            if (data is null) continue;
            Dictionary<int, string>? headers = null;
            uint previousRow = 0;
            var rowCount = 0;
            foreach (var row in data.Elements<Row>())
            {
                token.ThrowIfCancellationRequested();
                if (++rowCount > 50000) throw new InvalidDataException($"工作表“{sheetName}”超过 50000 行，请拆分资料。");
                var rowNumber = row.RowIndex?.Value ?? previousRow + 1;
                if (rowNumber <= previousRow) throw new InvalidDataException($"工作表“{sheetName}”行号顺序不正确，请重新另存文件。");
                previousRow = rowNumber;
                var cells = ReadRow(row, shared, styles, date1904);
                if (cells.Values.All(string.IsNullOrWhiteSpace)) continue;
                if (headers is null)
                {
                    headers = cells;
                    result.Add($"工作表：{sheetName} / 第 {rowNumber} 行（表头）",
                        string.Join("；", headers.Select(pair => $"{ColumnName(pair.Key)}列：{pair.Value}")));
                    continue;
                }
                // 列号取自 A3、C3 等真实地址；B3 缺失不会把 C 列的数据错配给 B 列名。
                var values = headers.Keys.Union(cells.Keys).Order().Select(column =>
                {
                    var title = headers.GetValueOrDefault(column);
                    var label = string.IsNullOrWhiteSpace(title) ? $"{ColumnName(column)}列" : $"{title}（{ColumnName(column)}列）";
                    return label + "：" + cells.GetValueOrDefault(column, "");
                });
                result.Add($"工作表：{sheetName} / 第 {rowNumber} 行", string.Join("；", values));
            }
        }
    }

    /// <summary>读取单行，保留稀疏单元格和各自的数据类型。</summary>
    /// <param name="row">包含 CellReference 的原始行。</param>
    /// <param name="shared">工作簿共享字符串表。</param>
    /// <param name="styles">用于识别日期和纯零补位的样式表。</param>
    /// <param name="date1904">是否采用 1904 日期系统。</param>
    /// <returns>从 1 开始的列号到可阅读值的对应关系。</returns>
    private static Dictionary<int, string> ReadRow(Row row, string[] shared, Stylesheet? styles, bool date1904)
    {
        var values = new Dictionary<int, string>();
        var previous = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            var address = cell.CellReference?.Value;
            var column = address is null ? previous + 1 : ColumnNumber(address);
            if (column <= previous || column > 16384) throw new InvalidDataException("XLSX 列地址重复、错序或超出范围，请重新另存文件。");
            previous = column;
            var value = ReadCell(cell, shared, styles, date1904);
            // 公式保存值可能不是最新计算结果，连同原文展示这条说明，不暗示服务重新计算过。
            if (cell.CellFormula is not null) value += "（公式保存值，未重新计算）";
            values.Add(column, value);
        }
        return values;
    }

    /// <summary>读取保存值，不执行公式；没有缓存值的公式必须先在 Excel 中计算并保存。</summary>
    /// <param name="cell">例如 C3 单元格。</param>
    /// <param name="shared">共享字符串集合。</param>
    /// <param name="styles">数字格式定义。</param>
    /// <param name="date1904">日期起点选择。</param>
    /// <returns>文字、日期或数字的可阅读表示。</returns>
    private static string ReadCell(Cell cell, string[] shared, Stylesheet? styles, bool date1904)
    {
        var raw = cell.CellValue?.InnerText ?? "";
        if (cell.CellFormula is not null && string.IsNullOrWhiteSpace(raw))
            throw new InvalidDataException($"XLSX 单元格 {cell.CellReference?.Value} 的公式没有保存计算结果，请在 Excel 中重新计算并保存；本服务不计算公式。");
        if (cell.DataType?.Value == CellValues.Error)
            throw new InvalidDataException($"XLSX 单元格 {cell.CellReference?.Value} 包含错误值，请修正后再保存。");
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (!int.TryParse(raw, out var index) || index < 0 || index >= shared.Length)
                throw new InvalidDataException("XLSX 共享字符串编号无效，请重新另存文件。");
            return shared[index];
        }
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.Boolean) return raw == "1" ? "true" : "false";
        if (cell.DataType is not null && cell.DataType.Value != CellValues.Number) return raw;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric)) return raw;

        var formats = styles?.CellFormats?.Elements<CellFormat>().ToArray() ?? [];
        var styleIndex = cell.StyleIndex?.Value ?? 0;
        if (styleIndex >= formats.Length) return raw;
        var formatId = formats[styleIndex].NumberFormatId?.Value ?? 0;
        var format = styles?.NumberingFormats?.Elements<NumberingFormat>()
            .FirstOrDefault(item => item.NumberFormatId?.Value == formatId)?.FormatCode?.Value ?? "";
        var dateFormat = Regex.Replace(format, "\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]", "").ToLowerInvariant();
        var isTimeOnly = formatId is 18 or 19 or 20 or 21 or 45 or 46 or 47 ||
            (Regex.IsMatch(dateFormat, "[hs]") && !Regex.IsMatch(dateFormat, "[yd]"));
        if (isTimeOnly)
        {
            var time = TimeSpan.FromDays(numeric);
            if (Regex.IsMatch(format, @"\[[hH]+\]"))
                return $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
            return time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        }
        if (formatId is >= 14 and <= 22 or >= 27 and <= 36 or >= 50 and <= 58 || Regex.IsMatch(dateFormat, "[ymd]"))
        {
            // 1900 系统的第 60 天是 Excel 虚构的日期，不能悄悄当成另一天。
            if (!date1904 && numeric >= 60 && numeric < 61) throw new InvalidDataException("XLSX 包含无效的 1900-02-29 日期，请更正。");
            var origin = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 31);
            var date = origin.AddDays(!date1904 && numeric >= 61 ? numeric - 1 : numeric);
            return date.ToString(numeric % 1 == 0 ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        // 编号常用 000000 显示前导零，保留这种简单格式；其他复杂数字格式保留原保存值。
        if (Regex.IsMatch(format, "^0{2,20}$") && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number.ToString(format, CultureInfo.InvariantCulture);
        return raw;
    }

    /// <summary>把 A3、AA12 等地址转换为列号，不把缺失的空白列挤掉。</summary>
    /// <param name="address">文件中的单元格地址。</param>
    /// <returns>A 为 1，AA 为 27。</returns>
    private static int ColumnNumber(string address)
    {
        if (!Regex.IsMatch(address, "^[A-Z]{1,3}[1-9][0-9]*$")) throw new InvalidDataException("XLSX 单元格地址无效。");
        var column = 0;
        foreach (var letter in address.TakeWhile(char.IsAsciiLetterUpper)) column = column * 26 + letter - 'A' + 1;
        return column;
    }

    /// <summary>把列号还原成字母，帮助用户在 Excel 中定位出处。</summary>
    /// <param name="column">从 1 开始的列号。</param>
    /// <returns>例如 3 返回 C。</returns>
    private static string ColumnName(int column)
    {
        var name = "";
        while (column > 0) { column--; name = (char)('A' + column % 26) + name; column /= 26; }
        return name;
    }
}
