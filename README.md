# MiniHarness：C# / .NET 9 最小 Agent

依据本目录的《Agent-Model-Harness架构与最小实现指南》实现可演进的 V0 运行框架：持久任务、跨轮预算、统一操作准入、等待与恢复、异步上下文准备、证据验收、运行事件和协议适配。真实进程、自动摘要与子任务调度留给后续能力迭代。文档是架构参考；本实现的范围以此 README 和代码为准。

`Agent = Model + Harness`。Model 返回文字和行动建议；Harness 校验、执行工具、记录真实结果、继续调用或停止。上游 Model 与对外 API 均支持 Responses、Chat Completions，两者独立选择。

## 项目与依赖

| 项目 | 职责 | 项目依赖 |
| --- | --- | --- |
| `MiniHarness.Abstractions` | Task/Turn/Step/Operation/Response、事实/历史/视图、资源/证据、运行契约 | 无 |
| `MiniHarness.Harness` | TaskRuntime、版本化存储、命令去重、执行与完成策略、协议门面及旧接口适配 | Abstractions |
| `MiniHarness.Protocols.OpenAI` | 两套协议与内部模型之间的双向转换 | Abstractions |
| `MiniHarness.Model.OpenAI` | ResponsesModelProvider、ChatCompletionsModelProvider；共用 HTTP、重试、错误处理 | Abstractions、Protocols |
| `MiniHarness.Server` | 两种 HTTP 入口、统一配置、DI 组合根、示例工具、SSE 输出 | 上述项目 |
| `MiniHarness.Tests` | 假模型确定性测试、故障注入、本地真实 HTTP 集成测试 | Server 及传递依赖 |

```text
调用方 ── /v1/responses ──────────┐
                                ├─ 协议转换 ─ ProtocolAgentService ─┐
调用方 ── /v1/chat/completions ──┘                                   │
调用方 ── /agent/tasks/* ── 任务命令与事件 ──────────────────────────┤
                                                               TaskRuntime
                                                                   ├─ IModelProvider
                                                                   ├─ IContextPreparation → IContextBuilder
                                                                   ├─ 工具快照 / 策略 / 执行器
                                                                   ├─ ITaskCompletionPolicy
                                                                   └─ ITaskStore / ITaskLease
```

Harness 不引用 Model、Protocols 或 Server；更换模型无需修改 TaskRuntime。所有入口共用同一个任务执行内核，旧 `AgentRunner` 是源码兼容适配器。协议转换仅有一份实现，两种 Model 适配器共用传输基类，主程序只在 `Program.Build` 中选择并注入实现。HTTP 层不加载、拼接历史，也不推进状态机。

目标框架为 `net9.0`，通过 `Directory.Build.props` 将语言限制为 **C# 10**。采用普通类、接口、构造函数、async/await、using 等常规语法；不使用 primary constructors、required、collection expressions 等较新写法。没有第三方 NuGet 依赖。

## 环境与启动

需要 **.NET 9 SDK**。仅安装运行时不能编译本项目。SDK 和运行时可以按版本并存。`global.json` 要求 9.0 SDK，并允许同一主版本的更新 feature band；安装的 .NET 10 SDK 会继续保留，但本项目不会选择它。

使用 Visual Studio 打开解决方案时，需要 **Visual Studio 2022 17.12 或更高版本**，并安装“ASP.NET 和 Web 开发”工作负载。推荐 VS 2022 17.14，匹配 .NET 9.0.3xx SDK。参见 [Microsoft 官方兼容矩阵](https://learn.microsoft.com/zh-cn/dotnet/core/porting/versioning-sdk-msbuild-vs)。

VS 2022 17.6（最初检查到的版本）的 MSBuild 为 17.6.3；需在 Visual Studio Installer 中更新至 17.12 或更高版本，才能正常加载、构建和调试本项目。降至 .NET 9 不会使 VS 17.6 获得 .NET 9 支持。IDE 更新前可用下面的 `dotnet` 命令构建、运行。

1. 安装 .NET 9 SDK，确认 `dotnet --list-sdks` 包含 `9.0.xxx`；在仓库根目录执行 `dotnet --version` 应选择 9.0 SDK。
2. 修改 `src/MiniHarness.Server/appsettings.json` 中的 Model 配置，或使用已设置的 `CODING_PLAN_URL`、`CODING_PLAN_APIKEY`、`CODING_PLAN_MODEL`。
3. 在仓库根目录运行：

```powershell
dotnet build MiniHarness.sln
dotnet run --project src/MiniHarness.Server
```

默认监听 `http://127.0.0.1:5080`，对外模型别名为 `mini-agent`。Model 保持默认空配置时，从进程、Windows 用户、Windows 系统变量依次读取完整的 `CODING_PLAN_*` 配置组；不会把环境变量中的真实密钥与显式配置的其他地址混用。`Model.Protocol` 仍由统一配置决定。缺少有效配置时启动报错，不猜测具体模型。

SDK 与已有版本统一安装在 `C:\Program Files\dotnet`。安装脚本默认安装 **SDK 9.0.319**，保留已有 SDK 和运行时；本项目通过 `global.json` 选择 9.0 SDK，无需切换 PATH。

需要在另一台机器以相同方式安装时，运行以下脚本，并接受 Windows 管理员授权提示：

```powershell
.\scripts\install-dotnet9.ps1
```

安装脚本从 Microsoft 官方发布元数据定位 x64 SDK 安装包，校验 SHA512 后以管理员权限运行离线安装程序，按版本并存。安装日志默认位于 `%TEMP%\miniharness-dotnet9-install.log`，脚本记录位于同名文件加 `.transcript.txt`。

## 所有配置放在一起

唯一配置文件是 `src/MiniHarness.Server/appsettings.json`。构建后复制到输出目录，发布后直接修改输出目录中的同名文件。更改源配置后使用 `dotnet run` 重新构建；运行中的配置不会热更新。

| 配置 | 作用 |
| --- | --- |
| `Urls` | 宿主监听地址 |
| `Server.ModelAlias` | 调用方使用的 Agent 名称，默认 `mini-agent` |
| `Server.ApiKey` | 调用本服务的 Bearer key；空值表示不鉴权，默认地址仅监听本机 |
| `Server.RequestTimeoutSeconds` / `MaxRequestBodyBytes` | 单个 HTTP 请求的总时间、请求体上限 |
| `Server.CancelOnDisconnect` | 默认 false；OpenAI 请求等待结束不取消任务，true 时请求取消传播到任务 |
| `Server.WorkspaceRoot` / `WorkspaceGrants` | 原生任务可使用的工作区边界和宿主授权；默认不开放工作区 |
| `Model.Protocol` | `Responses` 或 `ChatCompletions`，只影响上游 |
| `Model.BaseUrl` | API 根路径，通常以 `/v1/` 结尾，不附加 `responses` 或 `chat/completions` |
| `Model.Model` / `Model.ApiKey` | 上游模型 ID 和上游密钥，与 Server 的名称和密钥各自独立 |
| `Model.TimeoutSeconds` / `MaxRetries` | 每次上游请求超时、瞬时连接/429/5xx 的重试次数 |
| `Harness.MaxModelCalls` / `MaxToolCalls` | 创建任务时固定的累计调用上限，跨轮输入和恢复不重置 |
| `Harness.MaxTotalTokens` | 同一任务累计输入和输出 token 阈值 |
| `Harness.MaxContextCharacters` | 序列化上下文的字符容量上限，包含工具描述；超限受控停止 |
| `Harness.MaxOutputTokens` | 单次模型输出上限，与调用方提供的上限取较小值 |
| `Harness.StopWhenUsageUnknown` | 上游不报告用量时是否停止，默认 `true` |
| `Harness.ToolTimeoutSeconds` / `MaxToolOutputCharacters` | 工具超时、单次输出截断上限 |
| `Harness.SystemInstructions` | 宿主注入的固定指令 |
| `Tools.AllowedNames` | 允许执行的本地工具；每次调用都检查 |
| `Storage.Directory` | 检查点目录，支持绝对路径；相对路径基于构建/发布输出目录 |

环境变量和命令行可覆盖同一份配置，适合不把密钥写入源码：

```powershell
$env:Model__Model = 'your-model-id'
$env:Model__ApiKey = 'your-upstream-key'
$env:Server__ApiKey = 'your-local-service-key'
$env:Model__Protocol = 'ChatCompletions' # 或 Responses
dotnet run --project src/MiniHarness.Server
```

无论上游选哪一种，两个对外入口始终可用。四种组合都由集成测试覆盖。

## 调用

可直接使用 `examples/requests.http`，或执行：

```powershell
$headers = @{ Authorization = 'Bearer your-local-service-key' }
$body = @{
    model = 'mini-agent'
    input = '请使用 add 工具计算 2 + 3，再给出结果。'
} | ConvertTo-Json -Depth 10
Invoke-RestMethod 'http://127.0.0.1:5080/v1/responses' -Method Post `
    -ContentType 'application/json; charset=utf-8' -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($body))
```

Chat Completions 使用 `messages` 数组；Responses 使用 `input` 字符串或消息/函数调用项数组。两者可设置 `tools`、`tool_choice`、`temperature` 和对应的输出 token 上限。Chat Completions 也接受旧的 `max_tokens`，但不能与 `max_completion_tokens` 同时传入。

服务提供 `GET /healthz`、`GET /v1/models`、`POST /v1/chat/completions`、`POST /v1/responses`、`GET /v1/responses/{id}`，以及下文的原生任务控制与事件接口。对外 `model` 必须为配置中的别名，上游模型由服务端配置决定。

内置 `echo`、`add`、`utc_time` 三个示例工具。Harness 校验扁平对象参数、执行 allow list 检查和超时控制，将真实结果按 `call_id` 回填给模型。参数错误、未知工具、拒绝和工具超时可成为后续观察；外层取消、预算停止、持久化失败是终止路径。

`tool_choice` 在 Harness 中统一检查：`none` 禁止工具调用，`required` 要求至少一个调用，指定函数名时整批调用必须匹配该名称。违反约束的可执行或完成候选输出会保存为失败记录，并返回上游错误（HTTP 502），整批不执行工具。正常的截断、拒绝回答或内容过滤仍进入 WaitingForUser，不执行工具。完成一批本地工具处理后，后续模型调用切换为 `auto`，允许模型根据真实结果结束。

调用方传入的函数工具属于调用方，返回 `tool_calls` / `function_call`，服务端不会执行。名称不能与内置工具冲突。同一模型响应混合请求本地工具与调用方工具时，当前实现返回 HTTP 409，整批不执行，避免部分副作用。

Responses 可传 `previous_response_id` 引用历史父响应。ProtocolAgentService 加载该响应时点的完整历史并创建分支任务，不修改父任务；新请求 `instructions` 替换旧请求的 instructions。只有省略 `tools` 字段时才继承调用方工具，显式 `tools: []` 清空，非空数组替换。调用方必须补齐待处理工具结果，错误或重复 `call_id` 被拒绝。历史分支有独立预算；需要同一长期任务、连续预算时，应使用原生任务的 `Input` 命令。

Chat Completions 无服务端历史续接字段，后续请求由调用方携带 `messages` 历史。Responses 的 provider reasoning 项在使用 Responses 上游时保留；无法无损翻译到 Chat Completions 的 reasoning 输入会报错。

Responses 上游输出另外保存完整、有序的项目快照，用于同协议续接及检查点恢复；Harness 使用其中的文本和工具调用投影。回放快照时不再按 reasoning、文本、调用重新分组，也不重复追加投影内容，因此保留交错顺序、项目 ID、内容分块和元数据。旧检查点中仅有 reasoning 补充项的格式继续兼容，但旧格式已经丢失的顺序无法追溯恢复。完整 provider 快照不能直接交给 Chat Completions 上游；切换上游协议时需开启新历史。对外 API 仍返回 Agent 的最终可见输出，不公开内部 reasoning 快照。

## 控制与恢复边界

权威状态为 `AgentTaskState`：Task 保存目标、约束、工作区、累计预算、历史、轮次、步骤、操作、资源和证据；`RunState` 只是兼容接口与 OpenAI 响应的投影。默认自然回答只完成 Turn，Task 等待新输入；声明 `RequiredEvidence` 后，默认策略要求当前工作区的通过证据，才完成 Task。`ITaskCompletionPolicy` 可替换；旧 `ICompletionPolicy` 经适配继续使用。模型文字中的“测试通过”不会自动成为证据。

`FileTaskStore` 的任务快照具有 `schemaVersion=1` 和单调修订号；文件租约覆盖整个读取、推进和提交过程，阻止两个执行者同时推进同一任务。写入使用临时文件、Flush 和替换；旧 `resp_*.json` 兼容读取，首次提交写入新的 `task_*.json`，不覆盖原文件。未知格式版本明确拒绝。响应索引可从权威任务快照重建。

工具先经参数校验、策略检查，副作用之前提交 `OperationStarted`，结果与历史一起提交。准入和执行接收相同的任务/轮次/步骤/操作身份、工作区和能力快照。审批决定绑定具体操作和快照，执行前重新检查当前策略。执行结果与 `EffectState` 分开：失败、超时或取消不代表没有副作用；不确定操作进入 Reconciliation。重启不重放 Started 操作，需宿主核实后显式提交 `Reconcile`，再决定记录已有结果或确认无副作用后重新准入。重复命令通过 ID 和载荷指纹去重，同 ID 不同载荷返回冲突。

控制命令先验证身份和载荷，再发布中断信号；正在处理的重复命令共享结果，已提交的重复命令返回原收据，不再次取消执行。同 ID 不同载荷在发布信号前拒绝。命令执行按提交顺序排队，中断准入不等待长操作持有的执行锁。暂停后 Resume 可以继续；已完成轮次暂停后恢复到等待输入。

工作区、工具实现或策略版本改变时，尚未执行的旧操作被明确结束为 Rejected/Effect=None，结果回填历史，再基于新快照重新调用模型。旧步骤和审批记录保留，不替换旧快照、不把旧审批转移到新操作。新操作仍须重新准入和审批。

资源、产物和证据使用结构化引用并记录所属任务/操作。`IResourceManager` 按 Kind 注入，提供 Query/Cancel/Reconcile；取消前持久化 `cancellation_requested`，管理器返回确认后才更新状态和结果。无管理器时保留请求状态，不冒称进程已经终止。管理器必须保证取消和恢复核实可以安全重试，返回时保持资源身份及所有者。管理器结果可重载，终态任务仍允许管理资源。当前验证使用假资源管理器，真实进程管理器留给后续迭代。

`StartChild` 在父任务的已完成轮次边界预留预算、保存父子关系，再通过同一 `ITaskRuntime` 提交子任务 Start。子任务继承工作区、授权和约束；不能通过命令 Facts 扩权。父任务新调用扣除未结算的预算预留。`CollectChild` 在子任务完成轮次或进入终态后，先持久关闭子任务，再把实际用量和结构化结果一次性回传父历史并释放预留；父提交失败可安全重试，重复收集不重复入账。父取消传播给未收集的子任务。此处是可运行的父子契约，不含自动分解任务或多 Agent 调度。

证据可由可信宿主显式提交，比较需求、通过状态、退出码和工作区内容指纹；工作区外部修改使旧证据失效。该接口不代替真实测试执行器，自动测试工具仍属于 V1 能力。

`store=false` 关闭 Responses 的 API 检索和续接；Harness 的内部检查点仍写盘，用于故障诊断。检查点包含对话和工具输入输出，生命周期由部署方管理。示例没有跨用户记录隔离：服务密钥代表一个共享信任域。

OpenAI 入口达到预算限制返回 HTTP 429，需处理的等待返回 409，上游失败返回 502；响应头 `X-Agent-Task-Id`、`X-Agent-Run-Id`、`X-Agent-Status` 可辅助诊断。默认连接断开或请求等待超时只结束观察等待，任务继续受自身预算和模型超时约束；`Server.CancelOnDisconnect=true` 可选择传播取消。原生 `Pause` 中断后可 Resume；`Cancel` 是终态，不会因后来补交审批而重新运行。

累计 token 是基于上游报告的入账阈值。任务在每次新调用/执行前检查，并收紧剩余输出额度；最终一次输入可能越过阈值，重试或中断的未知计费无法伪造精确值。`MaxModelCalls` 独立限制总次数，网络重试另受 `MaxRetries` 限制。同一任务的 Input 和 Resume 使用持久累计预算；OpenAI 历史分支创建独立任务与预算。默认遇到未知模型用量时保守停止。

工具必须配合 CancellationToken 停止。框架等待执行器退出，不会超时后丢下可能继续产生副作用的后台任务。进程工具需要另外实现进程终止、回收和结果未知处理；示例没有不受控的 Shell 工具。

## 协议兼容范围

实现的是文本与 function calling 子集，并非完整 OpenAI 服务。支持两种协议的 JSON 返回、用量、工具请求/结果关联、拒绝回答和输出截断。上游采用非流式请求；下游 `stream=true` 提供**缓冲式 SSE**：Agent 完成后输出协议事件，不提供模型实时首 token 延迟。Chat SSE 输出 chunk、可选 usage 和 `[DONE]`；Responses SSE 输出 created、item/part、文本/参数 delta/done 和 terminal 事件。

未实现的 OpenAI 字段返回 400。当前不支持图像/音频、内置搜索工具、JSON Schema response_format、OpenAI background 参数、WebSocket、并行候选 `n>1`。原生任务已支持异步提交、顺序输入、等待、恢复和进度订阅；输入在当前推进结束后按顺序处理，持久化后才确认接受。异步准备阶段当前默认透传视图，真实摘要、检索、长进程、MCP 和子任务调度尚未实现。它们可以通过准备、工具、资源和任务契约增量接入。

## 原生长期任务与事件

任务命令使用内部的规范化消息字段 `text`（OpenAI 入口仍使用标准的 `content` / `input`）：

```http
POST /agent/tasks/commands?wait=false
Content-Type: application/json

{
  "id": "caller-generated-unique-command-id",
  "kind": "Start",
  "request": {
    "model": "mini-agent",
    "messages": [{ "role": "user", "text": "请使用 add 计算 2+3" }],
    "options": { "toolChoice": "add", "maxOutputTokens": 512 }
  }
}
```

`wait=false` 在命令接受记录持久化后返回 202、`taskId` 和 receipt；省略该参数则等待推进结果。之后使用同一 taskId 提交 `Input`，沿用任务身份及预算。可用命令有 `Start`、`Input`、`Approval`、`ExternalResult`、`Pause`、`Cancel`、`Resume`、`Evidence`、`RefreshWorkspace`、`Reconcile`、`QueryResource`、`CancelResource`、`ReconcileResources`、`StartChild`、`CollectChild`；需要的字段见 `TaskCommand`。资源命令使用 `resourceId`；创建子任务提供 `request` 和 `childBudget`，收集使用 `childTaskId`。输入、审批、外部工具结果和证据都有明确的等待状态及继续入口；未知副作用须先核实，不能用 Resume 绕过。

- `GET /agent/tasks/{taskId}`：读取持久任务、等待原因、待决操作、预算和资源引用。
- `GET /agent/tasks/{taskId}/events?after=0`：读取该游标之后的已提交事件。
- `GET /agent/tasks/{taskId}/events?after=0&follow=true`：原生进度 SSE，支持 `Last-Event-ID` 续读；断开订阅不取消任务。

事件由内核状态变化产生，与快照一起先提交后发布，包含任务、轮次、操作关联和单调序号；不是从最终回答反向拼装。单进程默认以短轮询观察已提交快照，不需要消息中间件。原生事件流独立于两种 OpenAI SSE 协议，不把内部工具事件伪装成 OpenAI 输出项。

可直接参考 `examples/task-commands.http`。任务命令字段使用规范化领域模型；OpenAI 标准请求仍使用原来的 `examples/requests.http`。

`Server.ApiKey` 同时保护 `/v1` 和 `/agent`。原生任务工作区只能位于配置的 `Server.WorkspaceRoot` 内，宿主授权来自 `WorkspaceGrants`。文件指纹包含未提交的普通文件内容，忽略 `.git`、`.data`、`bin`、`obj` 和链接项；调用方不能借工作区参数开放任意目录。默认未配置工作区时仅运行不需要文件访问的示例工具。

协议格式参考 [OpenAI 官方迁移说明](https://developers.openai.com/api/docs/guides/migrate-to-responses)、[函数工具说明](https://developers.openai.com/api/docs/guides/function-calling) 和 [SSE 说明](https://developers.openai.com/api/docs/guides/streaming-responses)。

## 验证

测试是零第三方依赖的可执行测试项目，失败时返回非零退出码：

```powershell
dotnet run --project tests/MiniHarness.Tests
# 或：构建 Release 并运行全部测试
.\scripts\verify.ps1
# 也可以显式指定系统 dotnet.exe
.\scripts\verify.ps1 -DotnetPath 'C:\Program Files\dotnet\dotnet.exe'
```

包括：工具反馈链路、参数修正、拒绝、取消与超时、预算、完成门槛、执行前后存储故障、调用 ID 去重、检查点回读、路径穿越拒绝、HTTP 重试分类，以及四种协议组合的真实本地 HTTP 请求、SSE、鉴权、客户端工具续接。回归用例还覆盖工具选择约束和整批拦截、两种上游下省略/清空/替换工具定义、混合 Responses 项目经文件检查点保存和加载后的完整原序回放，以及旧格式兼容。

测试使用本地假上游，不需要 API key，也不产生真实模型调用费用。真实模型的任务质量、供应商特有兼容性需要在配置真实接入后另外评估。

离线验证由保留的 26 项兼容测试与 9 个阶段集成场景组成，共 35 项。新增场景按交付批次组织，不按每个方法拆测试：

本轮 Release 构建结果为 **0 警告、0 错误，35/35 离线测试通过**；所有阶段均可单独选择执行。此前真实模型验证另有 2 个完整任务场景，结果见下文报告；本轮边界修复未调用真实模型。

| 交付批次 | 场景覆盖 |
| --- | --- |
| 1 | 旧检查点迁移、追加轮次、预算/原始项重载、租约冲突和未知版本拒绝 |
| 2 | 同任务两轮、并发串行、命令去重、观察等待取消和累计预算 |
| 3 | 审批继续、重复审批、上下文与资源归属、取消后审批拦截、能力版本变化、未知副作用核实 |
| 4＋5 | 异步准备取消与恢复、事实隔离、步骤快照、外部工作区修改和新旧证据验收 |
| 6 | HTTP 异步提交、断开订阅、事件续读、历史分支及服务重启后的同任务继续 |
| v0-control | 重复控制命令、载荷冲突隔离、及时中断、Resume、重启后去重与旧指纹兼容 |
| v0-replan | edit→inspect 工作区变化、旧操作失效反馈、新操作重新审批、继续输入 |
| v0-resource | 工具结束后资源继续运行、查询、取消失败重试、管理器确认、重载核实、任务取消传播 |
| v0-child | 统一运行层创建、父子身份/权限、预算预留、防超配、结果回传、结算失败重试、重复结算和父取消传播 |
| 7（显式在线） | 两种真实上游协议的工具闭环、文件重载、第二轮输入和连续预算 |

```powershell
.\scripts\verify.ps1 -Stage 1
.\scripts\verify.ps1 -Stage 2
.\scripts\verify.ps1 -Stage 3
.\scripts\verify.ps1 -Stage 4-5
.\scripts\verify.ps1 -Stage 6
.\scripts\verify.ps1 -Stage v0-control
.\scripts\verify.ps1 -Stage v0-replan
.\scripts\verify.ps1 -Stage v0-resource
.\scripts\verify.ps1 -Stage v0-child
.\scripts\verify.ps1                 # 全量离线回归，不调用真实模型
.\scripts\verify.ps1 -Stage 7        # 显式真实模型验收，会消耗模型额度
```

真实模型验证为独立的显式运行项目，不会被上述离线验证脚本自动调用：

```powershell
dotnet run --project tests/MiniHarness.LiveTests --configuration Release
```

该探针依次读取进程、Windows 用户、Windows 系统环境变量中的 `CODING_PLAN_URL`、`CODING_PLAN_APIKEY`、`CODING_PLAN_MODEL`。密钥仅用于内存中的认证请求，不写入项目配置或报告。它复用现有 Model 适配器和 Harness，分别验证两种协议的文本响应及一次本地加法工具闭环；每次模型输出上限为 512 tokens，不自动重试。报告默认写入被 Git 忽略的 `.data/validation/llm-validation.json`，可用第一个命令行参数指定其他报告路径。此项目会发起真实请求、消耗模型额度。

基础参数探针的旧报告保留在 `.data/validation/llm-validation.json`。新 V0 整体验收使用：

```powershell
dotnet run --project tests/MiniHarness.LiveTests --configuration Release -- --runtime
```

2026-10-17 实测 `doubao-seed-evolving`：Responses 和 Chat Completions 均完成“真实工具调用 → 本地计算 42 → 回填 → 保存 → 新建运行层与存储实例重载 → 同一任务第二轮回答 42”。两种协议各 3 次模型请求，全部 HTTP 200，共 4,372 tokens；身份保持、预算连续、两轮记录持久化。报告：`.data/validation/v0-runtime-validation.json`。每次最大输出 512 tokens、无自动重试；未做上下文及输出上限压力测试。主程序和探针共用 CODING_PLAN 配置读取实现，密钥不写入配置、检查点或报告。

## 扩展位置

- 新模型：实现 `IModelProvider`，在组合根注册，并给出不含密钥的模型/协议/配置版本绑定。
- 新工具：普通工具继续实现 `ITool`；需要执行上下文和结构化资源/证据时实现 `IContextualTool`。两者共用参数校验、超时、输出限制和操作记录。
- 上下文压缩：实现 `IContextPreparation`，异步生成有限视图；保留事实和原始历史，Builder 只组装已准备内容。
- 业务验收：实现 `ITaskCompletionPolicy`；旧 `ICompletionPolicy` 通过适配器复用。
- 权限与审批：实现 `IContextualToolPolicy` 并提供策略版本；旧 `IToolPolicy` 继续适配。审批 UI 后续接入任务命令。
- 能力快照包含工具定义、实现类型/二进制身份、`IVersionedTool` 配置版本及策略版本；改变工具配置应更新版本。等待后不执行与原快照不匹配的实现。参数校验使用冻结的工具 schema。
- 数据库存储：实现 `ITaskStore` / `ITaskLease`，保留单写者、版本提交、命令去重和未知结果不重放的契约。
- 长进程与子任务：实现 `IResourceManager` 接入真实资源；在已有 StartChild/CollectChild 和预留/结算契约上增加调度。当前没有宣称实现真实进程终止、自动摘要或多 Agent 调度。
