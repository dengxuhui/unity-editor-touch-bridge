# Unity Editor Touch Bridge — Architecture

## Overview

本插件采用**委托注入**架构，将 Editor-only 的网络层（WebSocket/HTTP）与 Runtime 核心逻辑完全解耦，保证用户 Player Build 零污染。

## 程序集边界

```
MobileBridge.Runtime  (无 Editor 依赖，编译进 Player)
MobileBridge.Editor   (依赖 Runtime + websocket-sharp，仅 Editor)
```

- `Runtime` 程序集通过 `#if UNITY_EDITOR` 静态委托槽与 Editor 通信，Player Build 时委托槽不存在。
- `Editor` 程序集在启动时绑定委托，关闭时清空，防止闭包持有已销毁对象。

## 模块职责

### Runtime/Core

| 文件 | 职责 |
|------|------|
| `MobileBridge.cs` | 主入口。管理生命周期（`StartBridge` / `StopBridge` / `IsActive`）。通过 `#if UNITY_EDITOR` 静态委托槽调用 Editor 网络层。 |
| `FrameCapturer.cs` | `WaitForEndOfFrame` 协程捕获帧。接受 `Action<byte[]> broadcastFrame` 与 `Func<int> clientCount` 注入，主线程 JPEG 编码后投入后台发送队列。 |
| `TouchReceiver.cs` | 接受 `subscribe` / `unsubscribe` 委托注入。解析触控 JSON，主线程消费 `ConcurrentQueue`。**双路注入**：New IS 路径通过 `InputSystem.QueueStateEvent` 注入虚拟 `Touchscreen`（`MOBILE_BRIDGE_INPUT_SYSTEM` 守卫）；Legacy 路径维护跨帧 `_legacyActive` 状态表，暴露 `LegacyTouches` 供 `LegacyTouchInput` 读取。 |
| `LegacyTouchInput.cs` | 继承 `BaseInput`，由 `MobileBridge` 自动挂载并设为 `StandaloneInputModule.inputOverride`。重写 `touchCount`、`GetTouch(i)` 等方法，将桥接触控喂入 EventSystem，无需 New Input System。 |
| `CoordinateMapper.cs` | 处理 Game View 黑边偏移、Y 轴翻转、Windows DPI 缩放（`Screen.dpi / 96f`）、macOS Retina（固定 2x）。 |

### Runtime/Capture

| 文件 | 职责 |
|------|------|
| `URPCaptureFeature.cs` | `ScriptableRendererFeature`。注入条件：`Base Camera && Game View && MobileBridge.IsActive`。`AddRenderPasses` 仅做 `EnqueuePass`；handle 访问在 `SetupRenderPasses` 中执行。 |

### Editor/Network

| 文件 | 职责 |
|------|------|
| `WebSocketServer.cs` | 命名空间 `MobileBridge.Editor`。管理 WebSocket 主连接（端口 8765）和 HTTP 静态文件服务（端口 8766）。对外暴露 `OnTouchMessage`、`OnClientConnected`、`OnHelloReceived` 事件及 `ClientCount` 属性。 |

### Editor

| 文件 | 职责 |
|------|------|
| `MobileBridgeWindow.cs` | Editor 控制面板。`StartBridge()` 创建 `WebSocketServer` 并绑定全部静态委托；`StopBridge()` 停止服务并清空所有委托槽。 |
| `SetupWizard.cs` | 检测 URP Renderer Asset，引导用户添加 `URPCaptureFeature`。 |
| `QRCodeGenerator.cs` | 生成二维码图像供面板展示。 |

## 帧捕获流程

```
① URPCaptureFeature.Execute()（主线程，AfterRendering）
        ↓
② WaitForEndOfFrame 协程（主线程）
        ↓ JPEG 编码（10-30ms，主线程可接受，无 GPU 阻塞）
③ BlockingCollection<byte[]>(capacity:2) 限流队列
        ↓
④ 后台常驻线程 → WebSocket Binary 帧发送
```

> 限流队列容量为 2，防止后台线程跟不上时内存无限堆积；超出时丢弃最旧帧。

## 触控注入流程

```
手机浏览器 Touch 事件
        ↓ getBoundingClientRect() 归一化 → JSON
WebSocket 上行（ws://host:8765）
        ↓ WebSocketServer.OnTouchMessage 事件
TouchReceiver.HandleMessage()（线程池线程）
        ↓ ConcurrentQueue<PendingTouch>（phase 用 UnityEngine.TouchPhase，无 IS 依赖）
TouchReceiver.Tick()（主线程，Update，[DefaultExecutionOrder(-1000)]）
        ↓ CoordinateMapper 坐标换算
        ├─[MOBILE_BRIDGE_INPUT_SYSTEM]─→ InputSystem.QueueStateEvent(virtualTouchscreen)
        │                                  （New IS 路径，InputSystemUIInputModule）
        └──────────────────────────────→ LegacyTouchInput.SetTouches()
                                           ↓ StandaloneInputModule.inputOverride
                                           （Legacy 路径，BaseInput override）
```

两条路径并行运行，`MobileBridge.StartBridge()` 时自动检测 `StandaloneInputModule`；若不存在则 Legacy 路径静默跳过。

## 委托绑定示意

```csharp
// MobileBridgeWindow.StartBridge()
MobileBridge.OnStartServer   = () => _server.Start();
MobileBridge.OnStopServer    = () => _server.Stop();
MobileBridge.OnGetClientCount = () => _server.ClientCount;
MobileBridge.OnBroadcastFrame = bytes => _server.BroadcastFrame(bytes);
MobileBridge.OnSubscribeTouch  = cb => _server.OnTouchMessage += cb;
MobileBridge.OnUnsubscribeTouch = cb => _server.OnTouchMessage -= cb;
// ... 其余委托
_bridge.StartBridge();

// MobileBridgeWindow.StopBridge()
_bridge.StopBridge();
MobileBridge.OnStartServer = null;
// ... 其余委托置 null
```

## 网络端口

| 端口 | 协议 | 用途 |
|------|------|------|
| 8765 | WebSocket (`ws://`) | 画面帧 Binary 下行 + 触控 JSON 上行 |
| 8766 | HTTP | 静态文件服务（`client.html`） |

## 零侵入保证

- `websocket-sharp.dll` 的 `.meta` 中 `Include Platforms` 仅含 `Editor`，不会被 Unity 打包进 Player。
- `MobileBridge.Runtime.asmdef` 的 `precompiledReferences` 不包含 `websocket-sharp`。
- `Runtime` 代码中所有 Editor-only 字段均受 `#if UNITY_EDITOR` 保护，Player Build 时编译器完全剔除。
- `com.unity.inputsystem` 为**可选依赖**，不在 `package.json` 的 `dependencies` 中。`MobileBridge.Runtime.asmdef` 通过 `versionDefines` 在包存在时定义 `MOBILE_BRIDGE_INPUT_SYSTEM`，所有 IS API 调用均在此符号守卫内；`Unity.InputSystem` 不再作为显式 `references` 条目（利用其 `autoReferenced: true` 特性自动注入）。
