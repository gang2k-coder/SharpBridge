# Plan:断点状态跟踪(pending/verified/failed)

日期:2026-08-01
分支:`fix/capture-breakpoints`

## 目标

在 SharpBridge 自有代码中维护断点状态:设置时快照之外,通过处理 SharpDbg 的 `BreakpointEvent` 同步"待绑定 → 已绑定"的翻转与行号调整,并修复 capture 断点的行调整盲区(④);`breakpoint_set`/`breakpoint_list` 向 agent 暴露真实状态。

## 背景:为什么需要

当前 SharpBridge 只在设置响应那一刻快照 `Verified`/`Message`,之后断点状态无人维护:

- `breakpoint_list` 里断点永远显示 `verified=false`(即使早已绑定并命中过)
- capture 断点落在不可执行行时,绑定的行调整信息走 `BreakpointEvent`,SharpBridge 未注册该事件 → `_bpConfigs` 键失配 → capture 静默退化为普通断点(审查发现的盲区 ④)
- agent 无法区分"待绑定 / 已绑定 / 永久失败"(僵尸断点不可见)

## 已确认事实(反编译 SharpDbg 0.1.6 / VSCodeDebugProtocol)

- `BreakpointEvent` 字段:`Id`/`Verified`/`Line`/`Column`/`EndLine`/`EndColumn`/`Offset`/`Message`/`Source`;函数断点时 Line/Source 等为 null,仅 Id/Verified/Message
- SharpDbg **只在绑定成功时**发 `BreakpointEvent`(`if (TryBindBreakpoint(item)) OnBreakpointChanged?.Invoke(item)`)——事件只会 pending → verified 正向翻转,失败的断点不发事件
- 事件 Id 与 SetBreakpoints 响应的 `Breakpoint.Id` 同源(BreakpointManager 的 `_nextBreakpointId`)
- 每次重设断点(SetBreakpoints/RemoveBreakpoint 重发)SharpDbg 都会创建新 Id
- pending 判定依据:Attaching 时目标模块必然未加载;"Breakpoint has not been processed by the debugger." 是 `_process == null` 时的固定消息

## 步骤

### 步骤 1:`BreakpointEntry` 模型扩展(DebugSession.cs)

- 新增 `public int? AdapterId { get; set; }` —— SharpDbg 的断点 Id,事件匹配用
- 新增 `public bool IsPending { get; set; }` —— 待绑定标记
- `Line` 改为可写:`public int Line { get; set; } = Line;`(record 位置参数重声明标准模式;若编译器报冲突,备选:`with` 替换 + 在 `_breakpointsByFile` 列表、`_bpConfigs`、AdapterId 映射三处同步更新引用)

### 步骤 2:AdapterId 映射表(DebugSession.cs)

- 新增 `private readonly Dictionary<int, BreakpointEntry> _bpsByAdapterId = [];`
- `SetBreakpoints` 响应循环与 `SetFunctionBreakpoints` 响应循环:`entries[i].AdapterId = bpResults[i].Id;`
- 两处响应处理末尾调用 `RebuildAdapterIdMap()`:清空后遍历 `_breakpointsByFile` 全部条目 + `_functionBreakpoints`,按 `AdapterId.HasValue` 重建——整表重建而非增量维护,覆盖 RemoveBreakpoint 重设时 Id 变化,不残留僵尸映射

### 步骤 3:pending 判定 + 状态派生

- 两处响应处理后:`entry.IsPending = !entry.Verified && (entry.Message == "Breakpoint has not been processed by the debugger." || CurrentState == SessionState.Attaching);`
- 新增静态派生:`public static string BreakpointStatus(BreakpointEntry e) => e.Verified ? "verified" : e.IsPending ? "pending" : "failed";`

### 步骤 4:`BreakpointEvent` 处理器(DebugSession.cs)

- 构造函数注册:`_host.RegisterEventType<BreakpointEvent>(OnBreakpointChanged);`
- `OnBreakpointChanged(BreakpointEvent e)`(reader 线程):
  1. `_bpsByAdapterId.TryGetValue(e.Breakpoint.Id, out var entry)` → miss 则 debug 日志后返回
  2. `entry.Verified = e.Breakpoint.Verified; entry.IsPending = false; entry.Message = e.Breakpoint.Message;`(事件携带 Message,已确认)
  3. 行号同步 + capture 键重新映射(修盲区 ④):
     ```csharp
     if (e.Breakpoint.Line.HasValue && e.Breakpoint.Line.Value != entry.Line)
     {
         var oldKey = (NormalizePath(entry.FilePath), entry.Line);
         entry.Line = e.Breakpoint.Line.Value;
         // EndLine/EndColumn 有值则同步
         if (_bpConfigs.Remove(oldKey))
             _bpConfigs[(NormalizePath(entry.FilePath), entry.Line)] = entry;
     }
     ```
  4. 注释说明线程模型:事件在 reader 线程写,与 MCP 线程的 `breakpoint_list` 读是既有并发模式;可选加固(默认不做):`_bpConfigs` 换 `ConcurrentDictionary`

### 步骤 5:工具层反馈(BreakpointTools.cs)

- `breakpoint_set`:响应加 `status = BreakpointStatus(entry)`;`hint` 三分支(verified 现有 / pending "target module isn't loaded yet, will bind on module load" / failed 用 `entry.Message` + "Check the source path/line against the debuggee's PDB.");failed 时 `message` 优先展示 `entry.Message`
- `function_breakpoint_set`:同样加 `status` + pending hint
- `breakpoint_list`:每条加 `status`;pending 附注 "May remain pending forever if the module never loads."

### 步骤 6:测试

- E2E test 11 增强(模块未加载场景):两个断点设置后、continue 前断言 `status == "pending"`;continue 返回 stopped 后断言 `status == "verified"` 且 capture 断点 `line == actualCounterLine`
- E2E test 3 增强(主会话,空行 23 → 调整到 24):continue 后 `breakpoint_list` 断言 `verified == true` 且 `line == 24`
- SharpBridge.Tests 新增:capture 断点落在空行 → 模块加载后绑定并调整行 → 命中时自动捕获、快照 line 为调整后行(直接验证盲区 ④)

### 步骤 7:构建 + 回归

- `dotnet build SharpBridge`(0 警告 0 错误)
- 串行跑三个套件(并行构建踩过 obj 文件锁):`SharpBridge.Tests` → `SharpBridge.LaunchTest` → `SharpBridge.E2ETests`(期望 21/21)

### 步骤 8:提交

- 单 commit(含本计划文档 + 实现 + 测试),消息草稿:
  > feat: track breakpoint verification state via BreakpointEvent
  > - BreakpointEntry gains AdapterId/IsPending; Line becomes writable
  > - _bpsByAdapterId rebuilt after every set/remove (adapter re-ids on re-set)
  > - BreakpointEvent handler syncs verified + adjusted line, re-keys _bpConfigs —
  >   fixes capture breakpoints set early on non-executable lines degrading to plain breaks
  > - breakpoint_set/function_breakpoint_set/breakpoint_list expose status
  >   (verified/pending/failed) with honest hints; zombies are now visible
  > - E2E asserts pending→verified transition; SharpBridge.Tests covers adjusted-line capture
- 不含 `.gitignore`/`.pi/`

## 涉及文件

| 文件 | 改动 |
|---|---|
| `SharpBridge/Services/DebugSession.cs` | 模型 +2 属性、映射表、事件注册与处理器、状态派生(~70 行) |
| `SharpBridge/Tools/BreakpointTools.cs` | 三个工具响应加 status/hint(~25 行) |
| `SharpBridge.E2ETests/Program.cs` | test 3/11 增补断言(~15 行) |
| `SharpBridge.Tests/Program.cs` | 新增 adjusted-line capture 用例(~30 行) |
| `docs/superpowers/plans/2026-08-01-breakpoint-state-tracking-plan.md` | 本计划 |

## 明确不做(本轮范围外)

- 僵尸断点自动检测(仅"可见化":pending 持续显示,附注说明)
- `_bpConfigs` 并发加固(ConcurrentDictionary)
- 工具层支持同文件多断点(单次调用传多行)
