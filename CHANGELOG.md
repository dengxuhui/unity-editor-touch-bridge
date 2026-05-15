# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- 文档更新：`README.md` 新增"项目状态"，明确本项目已停止维护（Maintenance Stopped），定位为研究项目并暂时搁置
- 文档更新：`README.md` 新增"视频链路架构限制（关键）"，说明 `WebSocket(TCP) + JPEG` 在弱网场景下的架构性冻结风险
- 文档新增：`Documentation~/REMOTE_CONTROL_PROTOCOL_ANALYSIS.md`，系统对比本项目与云游戏/Chrome/成熟远控方案的协议差异，并给出"彻底解决需架构级重构（WebRTC 等实时媒体链路）"结论

## [0.1.0] - 2026-05-14

### Added

#### P0 基线对齐
- 全量源码实现：`Runtime/Core`、`Runtime/Capture`、`Runtime/Network`、`Editor`、`WebClient` 完整目录结构
- `MobileBridge.cs`：主入口，管理 Bridge 生命周期（`StartBridge` / `StopBridge` / `IsActive`）
- `FrameCapturer.cs`：`WaitForEndOfFrame` 捕获帧，主线程 JPEG 编码，后台线程发送
- `TouchReceiver.cs`：解析归一化触控坐标并通过 `InputSystem.QueueStateEvent` 注入虚拟 `Touchscreen`
- `CoordinateMapper.cs`：处理 Game View 黑边偏移、Y 轴翻转、DPI/Retina 缩放
- `URPCaptureFeature.cs`：`ScriptableRendererFeature`，仅对 `Base Camera + Game View + IsActive` 注入
- `WebSocketServer.cs`（Editor only）：WebSocket 主连接（端口 8765）+ HTTP 静态文件服务（端口 8766）
- `MobileBridgeWindow.cs`：Editor 控制面板，显示连接状态、IP 地址、调试信息
- `SetupWizard.cs`：自动检测 URP Renderer Asset 并引导添加 `URPCaptureFeature`
- `QRCodeGenerator.cs`：生成二维码供手机扫码访问
- `WebClient/client.html`：手机端单文件网页，统一使用 `ws://`，Canvas 1:1 渲染帧画面

#### P1 基础链路打通
- 引入 `websocket-sharp.dll`（Editor only，置于 `Editor/Plugins/`）
- 端到端闭环：扫码 → 打开页面 → 画面推流 → 触控回传
- 连接状态管理：启动、断开、无客户端时空发送保护

#### P2 平台稳定性
- 协议统一为 HTTP + WS（移除原 HTTPS/WSS 方案及 BouncyCastle 依赖）
- iOS Safari / Android Chrome 均通过 `ws://` 直连，零配置
- 坐标映射加固：黑边偏移、Y 轴翻转、Windows DPI 缩放（`Screen.dpi / 96f`）、macOS Retina 固定 2x
- 多指追踪：`touch id` 生命周期管理，支持 `began / moved / ended / cancelled`
- 断线重连：切后台、锁屏、网络切换后可恢复连接
- `URPCaptureFeature` 修复：`AddRenderPasses` 只做 `EnqueuePass`，handle 访问移至 `SetupRenderPasses`

#### P3 性能与体验
- `client.html` 画面清晰度修复：canvas 位图尺寸按每帧 `bitmap.width/height` 同步（1:1 绘制），CSS `object-fit: contain` 负责视口适配
- JPEG Quality 可配置（`EncodeToJPG(jpegQuality)` 完整链路）
- Editor 面板：连接状态实时显示、IP/端口一键复制、Game View 焦点提示

#### P4 发布准备
- `WebSocketServer` 迁移至 `Editor/Network/`（命名空间 `MobileBridge.Editor`），`websocket-sharp.dll` 限定 Editor only
- `Runtime` 程序集完全移除 `websocket-sharp` 依赖，零侵入用户 Player Build
- `MobileBridge.cs` 改为委托注入架构：`#if UNITY_EDITOR` 静态委托槽，Editor 启动时绑定
- `FrameCapturer` / `TouchReceiver` 构造函数改为 `Action` / `Func` 注入，解除对 `WebSocketServer` 的直接依赖
- 移除 `Samples~/` 目录
- `package.json` 补充 `"license": "MIT"` 字段
- 补充 `CHANGELOG.md`、`README.md`、`Documentation~/ARCHITECTURE.md`
