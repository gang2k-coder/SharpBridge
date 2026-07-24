> **Status: FIXED** (commit fd17ca3). Root cause: `ContinueAndWaitAsync` sent a redundant `ContinueRequest` after attach because SharpDbg's `ConfigurationDone` already auto-continued the process. ICorDebug rejected it with `CORDBG_E_SUPERFLOUS_CONTINUE`. Fix: skip the initial `ContinueRequest` when `_lastStop.Reason == "attach"`.

```mermaid
sequenceDiagram
    participant Client as E2E Test Client (进程 A)
    participant Server as MCP Server 线程 (进程 B)
    participant Reader as DAP Reader 线程 (进程 B)
    participant SharpDbg as SharpDbg / ICorDebug

    Client->>Server: debug_continue
    activate Server
    Server->>Server: ContinueAndWaitAsync()
    Server->>Server: _host.SendRequestSync(ContinueRequest)
    Note over Server: 阻塞在 await stopTcs.Task... ⏳

    Note over SharpDbg: 程序继续执行...
    SharpDbg->>SharpDbg: 断点命中！

    SharpDbg-->>Reader: BreakpointCorDebug 回调
    activate Reader
    Reader->>Reader: OnStopped(e)
    Reader->>Reader: CurrentState = Stopped
    Reader->>Reader: old TCS.TrySetResult(e) 🔓
    deactivate Reader

    Server<<->>Reader: TCS 完成，ContinueAndWaitAsync 唤醒
    Server->>Server: return BuildStopEvent(stopEvent)
    Server-->>Client: JSON {"status":"stopped","threadId":39220}
    deactivate Server

    rect rgb(255, 200, 200)
        Note over Reader,SharpDbg: ⚠️ Reader 线程还在处理后续 ICorDebug 回调
        SharpDbg-->>Reader: LoadAssembly callback
        SharpDbg-->>Reader: EvalComplete callback
        SharpDbg-->>Reader: CreateThread callback
        Note over Reader: 回调未处理完，Reader 忙

        Client->>Server: stacktrace_get(threadId=39220)
        activate Server
        Server->>Server: GetStackTrace(39220)
        Server->>Reader: _host.SendRequestSync(StackTraceRequest)
        Note over Server: 阻塞等待响应...

        Note over Reader: ❌ Reader 正在处理回调
        Note over Reader: SendRequestSync 等 Reader 读响应
        Note over Reader: 但 Reader 卡在其他回调里
        Note over Reader,Server: 💀 Deadlock 或 SharpDbg 返回空

        Server-->>Client: {"count":0,"frames":[]} 或 error
        deactivate Server
    end

    rect rgb(200, 255, 200)
        Note over Client,Server: ✅ 单元测试：无进程边界

        Note over Server: ContinueAndWaitAsync() → return
        Note over Server: GetStackTrace() — 同一线程顺序执行
        Note over Reader: Reader 有充足时间清空事件队列
        Note over Server: Reader 已空闲，SendRequestSync 顺利返回
    end
```

**参与者说明：**

| 生命线 | 线程 | 职责 |
|--------|------|------|
| E2E Test Client | 进程 A，独立线程 | 发 MCP 请求，等 JSON 响应 |
| MCP Server 线程 | 进程 B | 处理 MCP 工具调用 → DebugSession 方法（ContinueAndWaitAsync, GetStackTrace 等都跑在这里） |
| DAP Reader 线程 | 进程 B | `DebugProtocolHost.Run()` 的消息循环，ICorDebug 回调 → OnStopped/OnExited |
| SharpDbg / ICorDebug | 进程 B 内 | .NET 调试引擎 |

**关键：只有两条线程。** `ContinueAndWaitAsync` 不是独立线程——它是 Server 线程上被阻塞的异步方法，等 Reader 线程完成 TCS 后恢复执行。

**根因：** Reader 线程一次只能做一件事。当它处理 ICorDebug 回调中，Client 发来新的 DAP 请求 → Server 线程调 `SendRequestSync` 阻塞 → 需要 Reader 读响应 → 但 Reader 还在处理上一个回调 → 僵住了。
