// 用途：检查回答中的带单位数值必须有引文，测试文字均为人工示例。
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Questions;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>机械核验只覆盖数值，不把测试结果宣传为任意回答都正确。</summary>
public sealed class EvidenceVerifierTests
{
    /// <summary>通道编号不能变成温度上限；引文没有带单位数量时应拒绝模型新增的数值。</summary>
    [Theory]
    [InlineData("报警上限为 80 度", "这里只说明通道编号 CH8。", false)]
    [InlineData("采样间隔为 5 秒", "未规定默认采样间隔。", false)]
    [InlineData("采样间隔为 5 秒", "采样间隔为5秒。", true)]
    [InlineData("记录时间为2024年6月", "记录时间：2024年6月", true)]
    [InlineData("先启用 CH1，再开始采集", "先启用 CH1，再开始采集。", true)]
    public void QuantitiesMustAppearInCitedText(string answer, string excerpt, bool expected)
    {
        Assert.Equal(expected, EvidenceVerifier.QuantitiesSupported(answer, [new Citation("doc", "示例.md", "正文", excerpt)]));
    }
}
