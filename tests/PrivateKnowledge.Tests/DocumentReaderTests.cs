// 用途：验证真实格式的提取与出处、表头关系、错误边界；不使用模型判断解析是否成功。
using System.Text;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Knowledge;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>格式解析回归测试；文件均由测试现场生成，不需要 Office 或网络。</summary>
public sealed class DocumentReaderTests
{
    /// <summary>用独立生成器制作的中文 PDF 验证中文映射，不依赖 Windows 字体或真实模型。</summary>
    [Fact]
    public void IndependentlyGeneratedChinesePdfKeepsTextAndPages()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", "中文操作指南.pdf"));
        var document = DocumentReader.Read("a", "中文.pdf", bytes, 1400);
        Assert.Equal(3, document.Chunks.Count);
        Assert.All(document.Chunks, chunk => Assert.Contains("设备连接成功后，下一步先启用采集通道。", chunk.Text));
        Assert.Equal(new[] { "第 1 页", "第 2 页", "第 3 页" }, document.Chunks.Select(chunk => chunk.Section));
    }

    /// <summary>Word 的自定义标题、中文正文和表格行必须保留所属关系。</summary>
    [Fact]
    public void WordKeepsHeadingAndTableLabels()
    {
        var document = DocumentReader.Read("alpha", "指南.docx", DocumentFixtures.Word(), 1400);
        Assert.Contains(document.Chunks, c => c.Section == "温度采集操作指南" && c.Text.Contains("启用采集通道"));
        Assert.Contains(document.Chunks, c => c.Section.Contains("表格 1 / 第 2 行") && c.Text.Contains("项目：温度监控；操作：设备连接成功"));
        Assert.Contains("温度采集操作指南", document.Text);
        Assert.All(document.Chunks, c => Assert.Equal("指南.docx", c.Title));
    }

    /// <summary>Excel 的 C 列不能挤到 B 列；日期、编号和公式保存值必须正确。</summary>
    [Fact]
    public void SpreadsheetKeepsSparseColumnsAndSavedValues()
    {
        var document = DocumentReader.Read("alpha", "规则.xlsx", DocumentFixtures.Spreadsheet(), 1400);
        var row = Assert.Single(document.Chunks.Where(c => c.Section == "工作表：操作规则 / 第 9 行"));
        Assert.Contains("项目（A列）：温度监控", row.Text);
        Assert.Contains("操作（C列）：设备连接成功", row.Text);
        Assert.Contains("日期（D列）：2023-03-15", row.Text);
        Assert.Contains("编号（E列）：000042", row.Text);
        Assert.Contains("数量（F列）：3", row.Text);
        Assert.Contains("公式保存值，未重新计算", row.Text);
        Assert.DoesNotContain("另一张表", row.Text);
        Assert.Contains(document.Chunks, c => c.Section.Contains("补充说明") && c.Text.Contains("另一张表"));
    }

    /// <summary>1904 日期系统的零值表示 1904-01-01，不是 1899 年。</summary>
    [Fact]
    public void SpreadsheetHonors1904DateSystem()
    {
        var document = DocumentReader.Read("alpha", "规则.xlsx", DocumentFixtures.Spreadsheet(date1904: true), 1400);
        Assert.Contains(document.Chunks, c => c.Text.Contains("日期（D列）：1904-01-01"));
    }

    /// <summary>自定义日期、一天内时间和超过一天的时长不能相互混淆。</summary>
    /// <param name="number">Excel 保存的数字。</param>
    /// <param name="format">自定义数字格式。</param>
    /// <param name="expected">应显示的可阅读值。</param>
    [Theory]
    [InlineData("0.5", "h:mm", "12:00:00")]
    [InlineData("1.25", "[h]:mm:ss", "30:00:00")]
    [InlineData("45000", "yyyy年m月d日", "2023-03-15")]
    public void SpreadsheetFormatsDateTimeAndDuration(string number, string format, string expected)
    {
        var document = DocumentReader.Read("a", "规则.xlsx", DocumentFixtures.SpreadsheetWithNumberFormat(number, format), 1400);
        Assert.Contains(document.Chunks, c => c.Text.Contains("日期（D列）：" + expected));
    }

    /// <summary>合并表格和缺少公式保存值必须失败，不能把错列或猜测结果放入索引。</summary>
    [Fact]
    public void UnsupportedOfficeStructuresFailClearly()
    {
        Assert.Contains("合并", Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", "规则.xlsx", DocumentFixtures.Spreadsheet(merged: true), 1400)).Message);
        Assert.Contains("公式没有保存计算结果", Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", "规则.xlsx", DocumentFixtures.Spreadsheet(missingFormula: true), 1400)).Message);
        Assert.Contains("合并", Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", "指南.docx", DocumentFixtures.Word(merged: true), 1400)).Message);
    }

    /// <summary>三页 PDF 应能按页引用，重复页眉页脚不进入正文。</summary>
    [Fact]
    public void PdfKeepsPageNumbersAndRemovesRepeatedEdges()
    {
        var document = DocumentReader.Read("alpha", "guide.pdf", DocumentFixtures.Pdf(), 1400);
        Assert.Equal(new[] { "第 1 页", "第 2 页", "第 3 页" }, document.Chunks.Select(c => c.Section).Distinct());
        Assert.All(document.Chunks, c => Assert.Contains("Enable a channel before collecting", c.Text));
        Assert.DoesNotContain("PrivateKnowledge manual", document.Text);
        Assert.DoesNotContain("Training copy", document.Text);
    }

    /// <summary>混合 PDF 中的扫描页不能静默跳过，否则操作员会误以为所有页面均已入库。</summary>
    [Fact]
    public void MixedPdfRequiresOcrForImageOnlyPage()
    {
        var error = Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", "mixed.pdf", DocumentFixtures.MixedImagePdf(), 1400));
        Assert.Contains("第 2 页", error.Message);
        Assert.Contains("OCR", error.Message);
    }

    /// <summary>旧格式继续保持标题、中文和代码块，代码块内的 # 不成为标题。</summary>
    [Theory]
    [InlineData("guide.md")]
    [InlineData("guide.txt")]
    public void PlainTextKeepsChineseAndCodeFences(string path)
    {
        var bytes = Encoding.UTF8.GetBytes("# 操作\n\n## 导入\n\n先核对容量。\n\n```text\n# 不是标题\n\n内容\n```\n");
        var document = DocumentReader.Read("a", path, bytes, 1400);
        Assert.Equal(Encoding.UTF8.GetString(bytes), document.Text);
        Assert.All(document.Chunks, c => Assert.Equal("操作 / 导入", c.Section));
        Assert.Contains("# 不是标题", document.Text);
        Assert.Contains("先核对容量", document.Text);
    }

    /// <summary>体积、页数、总文字上限都应在发布前生效，不静默裁剪资料。</summary>
    [Fact]
    public void LimitsRejectBeforePublishingPartialContent()
    {
        var word = DocumentFixtures.Word();
        Assert.Contains("MaxExpandedBytes", Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", "a.docx", word, 1400,
            new KnowledgeOptions { MaxExpandedBytes = 1 })).Message);
        Assert.Contains("MaxExtractedCharacters", Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", "a.docx", word, 1400,
            new KnowledgeOptions { MaxExtractedCharacters = 10 })).Message);
        Assert.Contains("MaxPdfPages", Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", "a.pdf", DocumentFixtures.Pdf(), 1400,
            new KnowledgeOptions { MaxPdfPages = 1 })).Message);
        // 长正文现在可按句子拆分；超长代码块仍须整体拒绝，不能截断命令或条件。
        Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", "a.md", Encoding.UTF8.GetBytes("```\n" + new string('字', 1500) + "\n```"), 1400));
    }

    /// <summary>损坏的二进制文件不会冒充成功解析；旧 Office 格式和临时文件不参与扫描。</summary>
    [Theory]
    [InlineData("a.docx")]
    [InlineData("a.pdf")]
    [InlineData("a.xlsx")]
    public void CorruptFilesFailAndTemporaryFilesAreIgnored(string path)
    {
        Assert.Throws<InvalidDataException>(() => DocumentReader.Read("a", path, Encoding.UTF8.GetBytes("损坏文件"), 1400));
        Assert.False(DocumentReader.Supports("~$操作.docx"));
        Assert.False(DocumentReader.Supports("旧资料.doc"));
        Assert.False(DocumentReader.Supports("旧表.xls"));
        Assert.True(DocumentReader.Supports("操作.DOCX"));
    }
}
