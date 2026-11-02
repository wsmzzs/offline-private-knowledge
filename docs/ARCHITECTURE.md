# 架构与代码阅读

## 各组件负责什么

| 组件 | 责任 |
| --- | --- |
| PrivateKnowledge.Desktop | Avalonia 界面、首次配置、进程拥有权、问答与原文展示 |
| PrivateKnowledge.Client | HTTP 数据结构、超时、错误、逐行下载协议 |
| PrivateKnowledge.Api | 目录扫描、解析、SQLite 快照、检索、模型调用、引用核验 |
| Ollama | 在服务电脑运行本地模型，管理模型文件 |
| SQLite | 保存可重建的派生数据；原始文件仍是事实来源 |

## 整理资料

服务稳定读取文件，计算内容指纹，按格式提取标题、自然段、表格或页码。片段包含文件名、章节与真实正文，再由嵌入模型生成向量。Markdown 表格每行重复表头；Word/Excel 保留字段名与行号。超过限制的结构明确报错。

每套知识库全部处理成功后原子发布快照并增加修订号。未改变的文件复用片段和向量；更换嵌入模型实际摘要、输入规则或解析规则触发重建。更新失败不把旧快照当作最新版回答。

## 一次提问

1. 客户端发送知识库 ID、原问题和本次选择的回答模型。
2. 服务确认资料就绪，限定在这一知识库内查询。
3. 两路检索分别寻找关键词和语义候选，以 RRF 合并排名。它按名次融合，不把两个量纲不同的分数机械平均。
4. 根据真实消息字节预算选择完整片段，不悄悄截掉业务条件。
5. 没有候选直接返回证据不足；有候选再把原问题与这些片段交给回答模型。
6. 检查结构、引用编号和部分具体数值；引用原文由服务取出，不采用模型自己编造的摘录。
7. 问答期间资料变化时拒绝返回旧答案；客户端提示重新提问。

Qwen 嵌入查询端带检索任务指令，资料端使用真实内容。嵌入只生成用于比较含义的数字，不训练回答模型，也不会让模型永远记住资料。

## 调度和资源

每个 API 实例把模型任务逐个执行。后台小批处理，在下一批开始前给已排队的提问让出机会；不会强行中断已经开始的一批。多个 API 实例或其他 Ollama 客户端之间没有全局队列。

CPU 嵌入减少显存需求，仍占系统内存；每批释放减少驻留但会重复加载。自动分配硬件并保留模型通常能改善连续处理速度，但需要更多驻留资源，应在目标机器上测量。

## 进程拥有权

`LocalEnvironment` 只有持有自己创建的 Process 对象时才能发送退出信号。复用已有 API 不取得它的控制权。企业模式只连接服务，关闭客户端不停止服务器。

首次配置复用相同连接逻辑，不建立第二个 API 管理器。只有检查和保存通过才写 `SetupCompleted=true`；下载失败或中断后下次继续引导。

## 阅读顺序

1. `src/PrivateKnowledge.Client/ApiModels.cs`：请求、回答、引用与健康状态。
2. `src/PrivateKnowledge.Desktop/ViewModels/MainViewModel.Setup.cs`：首次配置步骤和取消。
3. `src/PrivateKnowledge.Desktop/Services/LocalEnvironment.cs`：已有进程与自建进程的边界。
4. `src/PrivateKnowledge.Api/Knowledge/DocumentReader.cs` 与 `KnowledgeIndexer.cs`：解析、稳定读取和发布。
5. `src/PrivateKnowledge.Api/Retrieval/Retriever.cs`：候选、排名融合和预算。
6. `src/PrivateKnowledge.Api/Models/OllamaClient.Embeddings.cs`：问题指令、批量和驻留参数。
7. `src/PrivateKnowledge.Api/Models/AnswerPrompt.cs` 与 `Questions/QuestionService.cs`：回答约束和引用检查。

SQLite 索引是可重建数据；不应修改数据库来代替维护源资料。当前遍历比较向量适合小规模原型，未验证海量知识库和企业多人容量。
