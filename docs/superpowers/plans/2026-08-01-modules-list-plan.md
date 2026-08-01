# Plan: modules_list —— 列出已加载模块的 MCP 工具

日期:2026-08-01
分支:`feat/modules-list`
父提交:`2d98db6`(main,merge of fix/capture-breakpoints)

## 目标

新增 `modules_list` MCP 工具,返回当前调试进程已加载的模块列表(id/name/path)。让 agent 能回答"目标程序集加载了没有、从哪个路径加载"——直接补上 function breakpoint / pending 断点使用体验的盲区(现在 agent 无法区分"模块没加载"和"断点模式写错")。

## 背景:为什么需要

- 断点 `pending` 时,agent 只能盲等;模块列表能确认"目标模块是否已加载"(TestDebuggee.dll 没加载 → 断点 pending 是正常的;加载了还 pending → 模式写错)
- attach 后 agent 不知道进程加载了哪些程序集,排查"断点没命中"时缺关键上下文
- SharpDbg 会发 DAP `ModuleEvent`,SharpBridge 只是没消费——零 SharpDbg 改动即可实现基础版

## 已确认事实(反编译 SharpDbg 0.1.6 / VSCodeDebugProtocol)

- SharpDbg `OnModuleLoaded(id, name, path)` → DAP `ModuleEvent { Reason = new, Module = { Id, Name, Path } }`(DebugAdapter.cs:215)。**只在 LoadModule 回调时发**,没有 changed/removed 事件(SharpDbg 不追踪模块卸载)
- `ModuleEvent` 只填充 `Id`/`Name`/`Path`;`Id` 是 **string**(模块路径,`OnModuleLoaded?.Invoke(text, Path.GetFileName(text), text)`——id 与 path 同值)。符号状态(HasSymbols)在 SharpDbg 内部 `ModuleInfo.MetadataReader`,**未暴露到事件**
- SharpDbg **未实现** DAP `ModulesRequest`(反编译无 HandleModulesRequest)——不能用拉取式,只能靠事件累积
- VSCodeDebugProtocol 的 `ModuleEvent`:Reason(new=0/changed=1/removed=2), `Module{ Id(string), Name, Path, ... }`
- SharpBridge 当前注册事件:Stopped/Breakpoint/BreakpointChanged/Exited/Terminated/Output/Continued/Initialized——**无 ModuleEvent**
- 事件处理器运行在 DAP reader 线程,与 OnStopped 等并发 → 模块表需要线程安全(参照 `_pendingStopTcs` 的 Interlocked 风格或简单 lock)
- ⚠️ **待验证**:ICorDebug attach 到已运行进程时,已加载模块是否会通过 LoadModule 回调补发(E2E 实测:attach 后 continue,TestDebuggee.dll 加载时事件到达——已验证该路径;attach 时已存在的 System.Private.CoreLib 等是否补发,待 E2E 确认)

## 步骤

### 步骤 1:DebugSession 维护模块表(DebugSession.cs)

- 新增 record:`public record ModuleInfo(string Id, string Name, string Path);`(注意与 SharpDbg 内部类型区分命名,或直接叫 `LoadedModule`)
- 新增字段:`private readonly Dictionary<string, LoadedModule> _modules = [];`(按 Id)
- 构造函数注册:`_host.RegisterEventType<ModuleEvent>(OnModuleChanged);`
- 处理器:
  - `Reason == new`:幂等加入(已存在则更新)
  - `Reason == removed`:删除(SharpDbg 当前不发,代码留好)
  - 日志:`← ModuleEvent: name={Name} path={Path}`
- 清理:disconnect/detach/exit 路径清空 `_modules`(与断点清理同位置)

### 步骤 2:`modules_list` 工具(InspectionTools.cs 或新 ModulesTools.cs)

- 放 InspectionTools(与 threads_list/stacktrace_get 同族),签名:`ModulesList(int? processId = null, string? processName = null)`
- `[AllowedState(SessionState.Attaching, SessionState.Running, SessionState.Stopped)]`——运行中也允许查(断点 pending 时 agent 需要能查)
- 返回:
  ```json
  {
    "count": N,
    "modules": [
      { "id": "<路径>", "name": "TestDebuggee.dll", "path": "C:\\...\\TestDebuggee.dll" },
      ...
    ]
  }
  ```
- 描述里写明:模块来自 SharpDbg 的 LoadModule 事件;attach 后已加载模块应已就绪;仅含 id/name/path(符号状态暂不可得)

### 步骤 3:测试

**SharpBridge.Tests**(直连 DebugSession):
- 新步骤:attach → 设断点 → ContinueAsync(触发 TestDebuggee.dll 加载)→ 断言 `GetModules()` 含 `TestDebuggee.dll` 与 `System.Private.CoreLib.dll`,且 count >= 2
- 附加断言:断点 verified 前 modules_list 不含 TestDebuggee(可选,验证时序语义)

**E2E**(test 3 之后插入 test 3b):
- continue 停止后调用 `modules_list` → `count >= 2` 且存在 `name == "TestDebuggee.dll"`
- 断言字段命名 camelCase(id/name/path)

### 步骤 4:文档

- README Features 列表加 `modules_list`
- Known Issues 注明:模块信息仅来自 LoadModule 事件(只增不减,disconnect 时清空);符号/PDB 状态暂不提供

## 不做的事(本迭代)

- **不做** PDB/符号状态:需要改 SharpDbg(把 HasSymbols 填进 ModuleEvent.SymbolStatus 或加 DAP 字段)——SharpDbg 是 nuget 包,本仓库不能改;`use-local-sharpdbg` 分支有本地引用先例,列为后续增强
- **不做** changed/removed 跟踪:SharpDbg 不发,进程内模块一般也不卸载
- **不做** 模块内类型/成员枚举:SharpDbg 无此 API,是更大的功能

## 风险

- attach 时已加载模块是否补发 LoadModule 回调待实测;若**不补发**,modules_list 在 attach 后立即调用可能只含后续加载的模块——缓解:测试以 continue 后(模块加载事件已发生)为准,文档写明"首次 continue 后模块列表完整"
- ModuleEvent 在 reader 线程回调:处理器里只做字典操作(lock 保护),不做 DAP 请求
- 模块 Id 是 string 且等于路径:对外文档用 name/path,id 保留原始值

## 验收标准

- 三套件全绿:SharpBridge.Tests / LaunchTest / E2E(21 + 新增 3b)
- E2E 3b 断言 modules_list 包含 TestDebuggee.dll 且字段为 camelCase
- `modules_list` 在 stopped/running 状态可调,disconnect 后返回空
