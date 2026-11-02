// 用途：保存一次问题的显示状态。一个项目一组记录，只在本次客户端进程内保留。
using System.Collections.ObjectModel;
using PrivateKnowledge.Client;

namespace PrivateKnowledge.Desktop.ViewModels;

/// <summary>引用按钮携带的资料身份，点击旧回答时仍能核对所属项目和版本。</summary>
/// <param name="KnowledgeBaseId">回答对应的项目名。</param>
/// <param name="Revision">回答对应的资料修订号。</param>
/// <param name="Citation">服务返回的真实引用信息。</param>
public sealed record SourceLink(string KnowledgeBaseId, long Revision, SourceCitation Citation)
{
    /// <summary>按钮文字，不隐藏文件与章节信息。</summary>
    public string Label => $"{Citation.Title} · {Citation.Section}";
}

/// <summary>一问一答的界面数据，等待、失败和成功都显示在这一条中。</summary>
public sealed class QuestionTurn : ObservableObject
{
    /// <summary>当前回答文字，等待时先显示提示。</summary>
    private string _answer = "正在查找资料并等待模型回答……";
    /// <summary>当前阶段的中文标签。</summary>
    private string _status = "正在回答";
    /// <summary>本条是否完成且能复制回答。</summary>
    private bool _canCopy;
    /// <summary>用户提交的原问题。</summary>
    public string Question { get; }
    /// <summary>本条归属项目，切换界面不会改变它。</summary>
    public string KnowledgeBaseId { get; }
    /// <summary>展示的回答或错误说明。</summary>
    public string Answer { get => _answer; set => Set(ref _answer, value); }
    /// <summary>例如依据不足、需要补充条件、已取消。</summary>
    public string Status { get => _status; set => Set(ref _status, value); }
    /// <summary>只有正常业务结果可复制，等待提示不能当答案复制。</summary>
    public bool CanCopy { get => _canCopy; set => Set(ref _canCopy, value); }
    /// <summary>服务核验过的引用按钮，错误结果不添加虚构引用。</summary>
    public ObservableCollection<SourceLink> Sources { get; } = [];

    /// <summary>在点击发送时建立记录，立刻给用户等待反馈。</summary>
    /// <param name="question">已去除首尾空白的输入问题。</param>
    /// <param name="knowledgeBaseId">发送瞬间选中的项目，如 demo-a。</param>
    public QuestionTurn(string question, string knowledgeBaseId)
    { Question = question; KnowledgeBaseId = knowledgeBaseId; }
}
