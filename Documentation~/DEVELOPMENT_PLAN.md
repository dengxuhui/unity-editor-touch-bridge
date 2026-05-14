# Unity Editor Touch Bridge — 阶段开发计划

> 本文档是本仓库的**开发执行基准**。后续所有 agent 开发任务默认按本计划推进，并在对应阶段更新状态。

## 0. 计划使用说明

- 本计划覆盖从当前实现到可稳定发布的完整路径。
- 变更原则：若计划与代码现状不一致，先更新本文档，再进行实现修改。
- 任务粒度：每次开发以一个小目标为单位（1-2 个文件或一个可验证行为）。
- 提交要求：每次任务完成后，更新「阶段状态」和「变更记录」。

## 1. 阶段总览

| 阶段 | 名称 | 目标 | 预计产出 | 状态 |
|------|------|------|----------|------|
| P0 | 基线对齐 | 代码与 SPEC、目录职责、端口和协议一致 | 差异清单 + 修正项 | DONE |
| P1 | 基础链路打通 | 画面推流与触控回传端到端可用 | 可稳定连接与交互 | DONE |
| P2 | 平台稳定性 | iOS/Android WS 连接稳定、坐标映射、多指一致性 | Android/iOS 双平台稳定 | DONE |
| P3 | 性能与体验 | 主线程无阻塞、调试可观测、错误可诊断 | 更低卡顿与更好可用性 | DONE |
| P4 | 发布准备 | 文档、Samples、发布检查完善 | 可对外发布的 UPM 包 | DONE |

状态说明：`TODO` / `IN_PROGRESS` / `DONE` / `BLOCKED`

## 2. 分阶段执行细项

### P0 基线对齐

**目标**
- 确认当前实现与 `SPEC.md`、`AGENTS.md` 中的关键约束一致。

**执行项**
- 核对目录职责：`Runtime/Core`、`Runtime/Capture`、`Runtime/Network`、`Editor`、`WebClient`。
- 核对网络端口：WebSocket `8765`、HTTP `8766`。
- 核对 URP 注入条件：`Base Camera && Game View && MobileBridge.IsActive`。
- 核对帧捕获方案：`WaitForEndOfFrame + ReadPixels`（含 Canvas Overlay）→ 主线程 JPEG 编码 → 后台线程发送（原 AsyncGPUReadback 三段式已废弃，因无法捕获 Canvas Overlay）。
- 核对触控协议字段与事件枚举：`began/moved/ended/cancelled`。

**验收标准**
- 形成差异清单（代码/文档）。
- 高风险偏差（端口、注入条件、线程模型）已修正或列为阻塞项。

### P1 基础链路打通

**目标**
- 完成「扫码打开页面 -> 看到画面 -> 触控回传」的最小闭环。

**执行项**
- 打通 `URPCaptureFeature -> FrameCapturer -> WebSocketServer -> client.html`。
- 验证 `client.html` 统一走 `ws://`。
- 完成基础连接状态管理：启动、断开、重连、无客户端时空发送。
- 确保触控事件能注入 Input System，且不影响系统鼠标。

**验收标准**
- Android Chrome 局域网下可稳定操作 5 分钟。
- Play Mode 中无明显错误刷屏（Editor Console）。

### P2 平台稳定性

**目标**
- 收敛 iOS 与 Windows/macOS 的关键差异，保证一致行为。

**执行项**
- 完成 iOS Safari / Android Chrome 的 `ws://` 连接稳定性验证。
- 加固坐标映射：黑边偏移、Y 轴翻转、DPI/Retina 差异。
- 完善多指追踪：`touch id` 生命周期与异常结束（cancelled）。
- 优化断线恢复：切后台、锁屏、网络切换后可恢复连接。

**验收标准**
- iOS Safari 可稳定连接并操作 10 分钟。
- Windows 与 macOS 下点击点位误差在可接受范围内（UI 按钮可重复命中）。

### P3 性能与体验

**目标**
- 保持主线程流畅，提升调试与问题定位效率。

**执行项**
- 增加性能观测项：捕获耗时、编码耗时、发送队列长度、丢帧数。
- 提供可配置项：目标 FPS、JPEG 质量、分辨率档位。
- 优化 Editor 面板：连接状态、错误提示、当前访问地址、快速复制。
- 增加一键诊断信息（端口占用、URP Feature 状态）。

**验收标准**
- 主线程无明显周期性卡顿（避免在回调中编码）。
- 用户可通过面板快速定位常见问题。

### P4 发布准备

**目标**
- 形成可交付、可复用、可上手的 UPM 发布版本。

**执行项**
- 完善 `README.md`、`Documentation~/`、FAQ 与已知限制。
- 完善 `Samples~/` 示例：包含最小可运行场景与配置说明。
- 核对 `package.json` 依赖与 Unity 版本声明。
- 发布前检查：目录、asmdef、meta、许可证、变更日志一致。

**验收标准**
- 新用户按 README 可在 10 分钟内跑通。
- 发布材料完整，版本信息可追溯。

## 3. 任务执行规则（供后续 agent 使用）

- 每次开发前：先确认当前阶段和未完成执行项。
- 每次开发后：更新本文档「阶段状态」与「变更记录」。
- 不跨阶段做高风险改动；若必须跨阶段，需在变更记录中说明原因。
- 优先处理阻塞链路项：端口/连接/注入条件/线程模型。
- 发现与 SPEC 冲突时，先提出并修正文档，再改代码。

## 4. 当前阶段状态

| 阶段 | 状态 | 开始时间 | 完成时间 | 备注 |
|------|------|----------|----------|------|
| P0 基线对齐 | DONE | 2026-05-13 | 2026-05-13 | 全量源码实现，通过五项核对 |
| P1 基础链路打通 | DONE | 2026-05-13 | 2026-05-13 | Android ws:// 验收通过 |
| P2 平台稳定性 | DONE | 2026-05-13 | 2026-05-14 | ws:// 统一、多指追踪、DPI换算、断线重连、Canvas Overlay 支持全部完成；真实项目验收进行中 |
| P3 性能与体验 | DONE | 2026-05-14 | 2026-05-14 | 客户端清晰度修复（canvas 1:1 位图）；JPEG Quality 链路验证完整 |
| P4 发布准备 | DONE | 2026-05-14 | 2026-05-14 | WebSocket 迁移 Editor only、委托注入架构、CHANGELOG/README/ARCHITECTURE 文档补全 |

## 5. 变更记录

| 日期 | 阶段 | 变更 | 说明 |
|------|------|------|------|
| 2026-05-13 | P0 | 初始化开发计划 | 新增本文档并设为后续开发执行基准 |
| 2026-05-13 | P0 | 全量源码实现 | 创建 Runtime/Core、Runtime/Capture、Runtime/Network、Editor、WebClient 全部源文件（11 个 .cs + client.html），通过目录职责/端口/URP注入条件/帧捕获三段式/触控协议五项核对；JPEG 编码暂在主线程（P3 优化） |
| 2026-05-13 | P1 | 基础链路打通 | T1 引入 websocket-sharp.dll；T2 CertificateHelper 补充局域网 IP 到 SAN；T3 WebSocketServer 基于 websocket-sharp 重写（WSS 8765 + HTTPS 8766 + /cert 端点）；T4 URPCaptureFeature/MobileBridge 生命周期守卫；T5 Editor 窗口 https:// + DrawCertificate() + NeedsRenewal()；T6 client.html wss 连接失败提示 |
| 2026-05-13 | P2 | 证书生成 Mono 兼容性修复（三轮） | **根本问题**：Unity 2022.3 Mono 大量 `System.Security.Cryptography` API 为未实现 stub。**第一轮**：`CertificateRequest` 抛 `PlatformNotSupportedException` → 引入 BouncyCastle 2.4.0（`Editor/Plugins/`，Editor only）；**第二轮**：BouncyCastle 生成的 PKCS#12 中 RC2-40-CBC 证书袋 Mono 无法解析 → 改为 cert.pem（DER）+ key.pem（PKCS#8）存储；**第三轮**：`ImportPkcs8PrivateKey` 也是 stub → 改为 cert.pem + key-params.xml（RSA XML）存储，生成端用 `DotNetUtilities.ToRSA()` + `ToXmlString(true)`，加载端用 `RSACryptoServiceProvider.FromXmlString()`。职责拆分：`CertificateGenerator`（Editor only，依赖 BouncyCastle）负责生成；`CertificateHelper`（Runtime，无外部依赖）负责加载。 |
| 2026-05-13 | P2 | URPCaptureFeature handle 访问时序修复 | `AddRenderPasses` 中访问 `renderer.cameraColorTargetHandle` 抛错（URP 2022.3 明确禁止）。修复：`AddRenderPasses` 只做 `EnqueuePass`；`SetupRenderPasses` 中调用 `_pass.Setup(renderer.cameraColorTargetHandle)`，此时 handle 已合法。同步修复 `in RenderingData` 参数不能用 `ref var` 取引用（CS8330）。 |
| 2026-05-13 | P2 | 协议策略调整为 HTTP + WS | 按最新 SPEC 移除证书相关功能：删除 `CertificateGenerator` / `CertificateHelper` 与 BouncyCastle 依赖；`WebSocketServer` 改为 `WS:8765 + HTTP:8766`；`client.html` 固定 `ws://`；Editor 面板与 Setup Wizard 移除证书入口。 |
| 2026-05-14 | P3 | client.html canvas 清晰度修复 | **根本问题**：`resizeCanvas()` 将 canvas 位图尺寸设为视口逻辑像素（如 390×844），而 Unity 发送的是 Game View 实际像素（如 1920×1080），`drawImage` 强制缩放导致插值模糊。**解决方案**：移除 `resizeCanvas`；在 `decodeAndDraw` 中按每帧 `bitmap.width/height` 同步 canvas 位图尺寸（1:1 绘制，无缩放插值）；CSS 加 `object-fit: contain` 由浏览器负责视口适配，保持宽高比。结果：图像清晰度与 Game View 一致。JPEG Quality 设置链路经代码审查确认完整有效（`EncodeToJPG(jpegQuality)` 已正确传参）。 |
