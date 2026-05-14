# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目性质

这是一个 **Unity UPM 插件**，仓库根目录即为包根目录，通过 Git URL 安装到 Unity 项目。**没有传统的构建命令或测试命令**——所有构建和测试均在 Unity Editor 内进行。

- **包名**：`com.dengxuhui.unity-editor-touch-bridge`
- **Unity 最低版本**：2022.3 LTS
- **强依赖**：URP 14.0+（仅支持 URP）
- **可选依赖**：Input System 1.7.0+。通过 asmdef `versionDefines` 定义 `MOBILE_BRIDGE_INPUT_SYSTEM`，IS API 全部在此符号内守卫
- **开发用 Unity 工程**：`Sandbox~/`（`~` 后缀使 Unity 不导入，Library 等目录已被 `.gitignore` 屏蔽）

## 开发工作流

所有代码修改在当前仓库中进行，在 `Sandbox~/` 中打开 Unity Editor 验证。无 CLI 构建或测试命令。

## 架构

### 程序集边界（核心约束）

```
MobileBridge.Runtime  → 编译进 Player Build，不得含任何 Editor 依赖
MobileBridge.Editor   → 仅 Editor，依赖 Runtime + websocket-sharp.dll
```

Runtime 通过 `#if UNITY_EDITOR` 静态委托槽与 Editor 通信：`MobileBridge.cs` 中定义所有委托（`BroadcastFrameAction`、`RegisterTouchHandler` 等），`MobileBridgeWindow.cs` 在 `StartBridge()` 时绑定，`StopBridge()` 时置 null。Player Build 时委托槽被编译器完全剔除。

### 目录职责

| 目录 | 说明 |
|------|------|
| `Runtime/Core/` | 主入口 `MobileBridge.cs`（生命周期 + CaptureLoop 协程）、`FrameCapturer.cs`（后台发送队列）、`TouchReceiver.cs`（双路触控注入）、`LegacyTouchInput.cs`（`BaseInput` override）、`CoordinateMapper.cs`（坐标换算） |
| `Runtime/Capture/` | `URPCaptureFeature.cs`（空壳，保留兼容性，实际捕获已迁移至 CaptureLoop） |
| `Editor/Network/` | `WebSocketServer.cs`（websocket-sharp，**Editor only**） |
| `Editor/` | `MobileBridgeWindow.cs`、`SetupWizard.cs`、`QRCodeGenerator.cs`、`BridgeLogger.cs` |
| `WebClient/` | `client.html`（手机端单文件网页） |

### 帧捕获流程

当前实现使用 `WaitForEndOfFrame + ReadPixels`（**不是** AsyncGPUReadback）：

```
CaptureLoop 协程（主线程）
  yield return WaitForEndOfFrame        ← 此时 Canvas Overlay 已完整合成
  → ReadPixels（同步，~1-3ms）
  → EncodeToJPG（主线程，~2-5ms）
  → FrameCapturer.EnqueueJpeg()
        ↓ BlockingCollection（容量 2，超出丢弃旧帧）
  后台常驻线程 → WebSocket Binary 发送
```

`URPCaptureFeature` 保留为空壳，是因为 `AsyncGPUReadback` 在 `ScriptableRenderPass` 执行时 Canvas Overlay 尚未合成。切勿恢复 AsyncGPUReadback 方案。

### 触控注入流程

```
手机浏览器 touchstart/move/end/cancel
  → getBoundingClientRect() 归一化为 nx/ny [0,1]
  → WebSocket JSON 上行 (ws://host:8765)
  → WebSocketServer.OnTouchMessage（线程池）
  → TouchReceiver ConcurrentQueue（PendingTouch.phase: UnityEngine.TouchPhase，无 IS 依赖）
  → Tick()（主线程 Update，[DefaultExecutionOrder(-1000)]）
  → CoordinateMapper（Y 轴翻转 + 黑边偏移 + DPI 换算）
  ├─ [#if MOBILE_BRIDGE_INPUT_SYSTEM]
  │    ToISPhase() → InputSystem.QueueStateEvent(virtualTouchscreen)
  └─ 维护 _legacyActive 跨帧状态表（Stationary 持久化）
       → LegacyTouchInput.SetTouches()
       → StandaloneInputModule.inputOverride.GetTouch(i)
```

`MobileBridge.StartBridge()` 自动检测 `StandaloneInputModule`，找到则挂载 `LegacyTouchInput` 并设为 `inputOverride`（用户无感）；`StopBridge()` 时清理。两条路径独立，可同时运行。

### 网络端口

| 端口 | 协议 | 用途 |
|------|------|------|
| 8765 | WebSocket (`ws://`) | 画面帧 Binary 下行 + 触控 JSON 上行 |
| 8766 | HTTP | 静态文件服务（`client.html`） |

### 坐标换算细节

两次换算，顺序不能颠倒：
1. **手机端（JS）**：`getBoundingClientRect()`（CSS 尺寸）→ 归一化 `[0,1]`，不能用 `canvas.width`（像素分辨率）
2. **Unity 端（C#）**：减去黑边偏移 → Y 轴翻转（`1f - ny`）→ 乘以 Screen 尺寸；Windows 额外处理 `Screen.dpi / 96f`

## 零侵入硬性约束

- **任何 Editor-only 逻辑必须放 `Editor/`**，不得放 `Runtime/`
- `websocket-sharp.dll` 放 `Editor/Plugins/`，其 `.meta` 的 `Include Platforms` 仅含 Editor
- `MobileBridge.Runtime.asmdef` 的 `precompiledReferences` 不得包含 `websocket-sharp`
- 不得在 `Runtime/` 引入任何新的第三方 DLL（除非 Player Build 必需，且需在 commit 中说明）
- 不得在用户 `Assets/` 目录下创建任何文件；运行时临时文件只能写 `Application.persistentDataPath`
- `com.unity.inputsystem` **不得**加回 `package.json` 的 `dependencies`；IS 支持通过 `versionDefines` + `#if MOBILE_BRIDGE_INPUT_SYSTEM` 实现可选化，强制安装会给 Legacy-only 用户弹出 backend 配置对话框

## 关键设计文档

- `SPEC.md`：完整技术规格，包含各模块伪代码、性能指标、跨平台差异
- `Documentation~/ARCHITECTURE.md`：程序集边界、委托绑定示意、模块职责
- `Documentation~/DEVELOPMENT_PLAN.md`：阶段开发计划（当前所有阶段 P0–P4 均已 DONE）
- `AGENTS.md`：面向 AI 的开发约束与注意事项（与本文档互补）

## 触控数据协议

```json
{ "type": "touch", "eventType": "began|moved|ended|cancelled",
  "touches": [{ "id": 0, "nx": 0.45, "ny": 0.32 }] }
```

`type` 为 `"cfg"` 时是服务端向客户端推送配置（如 `showDebug`）。

## URPCaptureFeature 注入条件

注入需同时满足（`AddRenderPasses` 只做 `EnqueuePass`，handle 访问在 `SetupRenderPasses` 中）：
```csharp
isBaseCamera && isGameView && MobileBridge.IsActive
```
Scene View / Preview Camera 不注入。
