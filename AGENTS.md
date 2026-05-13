# AGENTS.md — Unity Editor Touch Bridge

## 项目性质

这是一个 **Unity UPM 插件**（非普通 C# 项目），仓库根目录即为包根目录，直接通过 Git URL 安装到 Unity 项目。没有 npm/cargo/go 等传统构建系统，没有独立的测试命令。

## 开发环境

- **包名**：`com.dengxuhui.unity-editor-touch-bridge`
- **Unity 最低版本**：2022.3 LTS
- **强依赖**：URP 14.0+、Input System 1.7.0+（仅支持 URP，Built-in RP / HDRP 无法使用）
- **开发用 Unity 工程**：`Sandbox~/`（不随包发布，`~` 后缀使 Unity 跳过导入，`.gitignore` 已屏蔽 Library 等生成目录）

## 目录职责（不要搞混）

| 目录 | 说明 |
|------|------|
| `Runtime/Core/` | 主入口 `MobileBridge.cs`、`FrameCapturer.cs`、`TouchReceiver.cs`、`CoordinateMapper.cs` |
| `Runtime/Capture/` | `URPCaptureFeature.cs`（ScriptableRendererFeature，挂在 URP Renderer Asset 上） |
| `Runtime/Network/` | `WebSocketServer.cs`、`CertificateHelper.cs`（无第三方依赖，基于 `System.Net.WebSockets`） |
| `Editor/` | `MobileBridgeWindow.cs`、`SetupWizard.cs`、`QRCodeGenerator.cs`（Editor Only asmdef） |
| `WebClient/` | `client.html`（手机端单文件网页，随包分发，运行时 serve） |
| `Sandbox~/` | 本地开发测试用 Unity 工程，**不是包的一部分**，`~/` 后缀使 Unity 不导入此目录 |
| `Documentation~/` | 文档，`~/` 后缀使 Unity 不导入此目录 |
| `Samples~/` | 示例场景，同上 |

## 网络端口（两个，不要只开一个）

- **8765**：WebSocket 主连接（画面帧 Binary 下行 + 触控 JSON 上行）
- **8766**：HTTP/HTTPS 静态文件服务（serve `client.html`，手机扫码后访问）

## iOS vs Android 差异（核心坑）

- **Android Chrome**：直接 `ws://`，零配置
- **iOS Safari**：必须 `wss://`，需先安装自签证书（`CertificateHelper` 自动生成，Setup Wizard 引导用户在 iOS 上安装，仅需一次，有效期 365 天）
- `client.html` 根据 `location.protocol` 自动选择 `ws:` 或 `wss:`，无需用户手动切换

## 坐标换算（两次，顺序固定）

1. **手机端（JS）**：用 `getBoundingClientRect()` 而非 `canvas.width`（CSS 尺寸 ≠ 像素分辨率），换算为归一化坐标 `[0,1]`
2. **Unity 端（C#）**：减去 Game View 黑边偏移，翻转 Y 轴（手机原点左上，Unity 原点左下）
3. **Windows 额外处理**：`Screen.dpi / 96f` 换算 DPI 缩放，macOS Retina 固定 2x

## 帧捕获架构（三步，不能合并）

```
① ScriptableRenderPass.Execute() → AsyncGPUReadback（非阻塞，主线程）
② onComplete 回调（主线程）→ 复制数据到托管内存，投入后台队列
③ 后台线程（常驻）→ JPEG 编码 → WebSocket 发送
```
不能在②中直接编码（主线程，JPEG 编码 10-30ms 会卡帧），不能用 `ReadPixels`（阻塞 GPU）。

## URPCaptureFeature 注入条件（三个条件同时满足才注入）

```csharp
bool isBaseCamera = renderingData.cameraData.renderType == CameraRenderType.Base;
bool isGameView   = renderingData.cameraData.cameraType == CameraType.Game;
// 且 MobileBridge.IsActive == true
```
在 Scene View / Preview Camera 不注入，避免误捕获。

## 触控数据格式

```json
{ "type": "touch", "event": "began|moved|ended|cancelled",
  "touches": [{ "id": 0, "nx": 0.45, "ny": 0.32 }] }
```
`nx`/`ny` 为归一化坐标，通过 `InputSystem.QueueStateEvent` 注入，系统鼠标不动。

## 零侵入原则（硬性约束，不得违反）

本插件是**纯编辑器辅助工具**，对用户项目的最终构建必须零影响。以下规则在任何情况下不得违反：

### 构建隔离

- **任何仅在 Editor 内使用的逻辑，必须放在 `Editor/` 目录或 Editor-only asmdef 中**，不得放在 `Runtime/`。
- `Runtime/` 下的代码和 DLL **会被编译进用户的最终游戏包（Android APK / iOS IPA / PC 构建）**，必须严格控制其内容。
- 新增第三方 DLL 时，必须判断其使用场景：
  - 仅 Editor 使用 → 放 `Editor/Plugins/`，不得放 `Runtime/Plugins/`
  - 运行时必需 → 放 `Runtime/Plugins/`，并在 PR/commit 中明确说明理由和体积影响
- **禁止**将证书生成、QR 码生成、UI 渲染、Setup Wizard 等 Editor-only 功能的依赖库混入 `Runtime/`。

### asmdef 边界

| asmdef | 允许的内容 | 禁止的内容 |
|--------|-----------|-----------|
| `MobileBridge.Runtime` | 运行时必需逻辑（WebSocket 收发、帧捕获、触控注入、证书**加载**） | 证书**生成**、Editor GUI、任何 `UnityEditor.*` 引用 |
| `MobileBridge.Editor` | 所有 Editor 窗口、向导、工具类、证书**生成** | 任何会被打包进玩家构建的逻辑 |

### 拆分原则（以 CertificateHelper 为范例）

当一个类同时包含"Editor 操作"和"运行时操作"时，必须拆分：

```
Runtime/Network/CertificateHelper.cs   ← 只保留 Load / Check（CertExists, NeedsRenewal, LoadOrCreate）
Editor/CertificateGenerator.cs         ← 只保留 Generate()，可依赖 Editor-only 第三方库
```

### 用户项目不可感知

- 插件不得在用户项目的 Player Settings、Build Settings、场景中自动写入任何持久化配置。
- 不得在用户的 `Assets/` 目录下创建任何文件；运行时生成的文件（证书、临时文件）只允许写入 `Application.persistentDataPath`（沙盒路径，不影响项目）。
- 不得修改用户项目的 URP Renderer Asset 或其他 ScriptableObject，除非用户通过 Setup Wizard **明确确认**。

## 设计文档

详细技术规格见 `SPEC.md`，包含：完整架构图、各模块伪代码、性能指标、黑边处理、跨平台差异、已知限制。**开发前务必阅读。**

## 阶段开发计划（执行基准）

后续开发请以 `Documentation~/DEVELOPMENT_PLAN.md` 为执行基准，按阶段逐步推进并更新状态。若计划与实现不一致，优先修正文档后再改代码。
