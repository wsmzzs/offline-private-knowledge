// 用途：真实 HTTP 验证企业密钥和管理边界；沿用虚构资料与测试模型，不访问公司数据。
using System.Net;
using PrivateKnowledge.Api.Configuration;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>远程开关、密钥和下载限制必须在服务器生效，不能只靠界面隐藏按钮。</summary>
public sealed class DeploymentTests
{
    /// <summary>企业配置必须显式开启，且不能用空值或弱密钥启动。</summary>
    [Fact]
    public void EnterpriseRequiresExplicitModeAndStrongKey()
    {
        new DeploymentOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new DeploymentOptions { Mode = "Enterprize" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DeploymentOptions { Mode = "Enterprise", ApiKey = "short" }.Validate());
    }

    /// <summary>没有密钥看不到资料或运行信息；正确密钥能问答，但不能管理模型。</summary>
    [Fact]
    public async Task SharedServiceRequiresKeyAndRejectsModelManagement()
    {
        await using var fixture = await ServiceFixture.StartAsync(enterprise: true);
        using var anonymous = new HttpClient { BaseAddress = fixture.Client.BaseAddress };
        foreach (var path in new[] { "health", "api/runtime", "api/knowledge-bases", "api/models", "swagger/index.html" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        anonymous.DefaultRequestHeaders.Add("X-Knowledge-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/knowledge-bases")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("api/runtime")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync("api/models/download-options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync("api/models/library")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync("api/models/download", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync("api/setup/download", new StringContent("{}"))).StatusCode);
        Assert.Equal("answered", (await fixture.AskAsync("alpha", "采集之前做什么？")).Status);
    }
}
