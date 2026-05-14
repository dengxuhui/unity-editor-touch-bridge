# Unity Editor Touch Bridge

一个 Unity UPM 插件，将 Unity Game View 实时串流至手机浏览器，并将手机触控事件直接注入 Unity Input System，用于在不打包的情况下测试移动端触控交互。

## 核心特性

- **手机端无需安装 App**，用浏览器直接访问即可
- **基于 WebSocket**，局域网零配置，无需信令服务器
- **双路触控注入**：自动适配 New Input System（`InputSystemUIInputModule`）与 Legacy Input Manager（`StandaloneInputModule`）
- **支持 Mac / Windows** 双平台开发机
- **支持 iOS Safari / Android Chrome（统一 `ws://`）**
- **零侵入用户构建**：所有网络依赖限定 Editor Only，不影响 Player Build

## 系统要求

- Unity 2022.3 LTS 或更高版本
- Universal Render Pipeline (URP) 14.0 或更高版本
- Unity Input System 1.7.0 或更高版本（**可选**）

> **注意**：本插件仅支持 URP，不支持 Built-in RP 或 HDRP。
>
> Input System 为可选依赖。安装后插件自动启用 New IS 路径（`QueueStateEvent` 虚拟 Touchscreen）；未安装时仅 Legacy 路径（`StandaloneInputModule` BaseInput override）生效。两条路径可同时运行。

## 安装

通过 Unity Package Manager，使用 Git URL 安装：

```
https://github.com/dengxuhui/unity-editor-touch-bridge.git
```

或在 `Packages/manifest.json` 中手动添加：

```json
{
  "dependencies": {
    "com.dengxuhui.unity-editor-touch-bridge": "https://github.com/dengxuhui/unity-editor-touch-bridge.git"
  }
}
```

## 快速开始

1. 安装插件后，打开菜单 **Window → Mobile Bridge → Setup Wizard**
2. Wizard 会自动检测 URP Renderer Asset 并添加 `URPCaptureFeature`
3. 点击 Play，打开 Mobile Bridge 面板，点击 **启动**
4. 手机扫描二维码或访问显示的链接，即可看到游戏画面并进行触控操作

## 项目结构

```
unity-editor-touch-bridge/
├── package.json                        # UPM 包描述
├── Runtime/                            # 运行时代码（零 Editor 依赖）
│   ├── Core/                           # 核心逻辑（MobileBridge, FrameCapturer 等）
│   ├── Capture/                        # URP ScriptableRendererFeature
│   └── MobileBridge.Runtime.asmdef
├── Editor/                             # 编辑器面板、Setup Wizard、二维码生成、WebSocket 服务器
│   ├── Network/                        # WebSocketServer（Editor only）
│   ├── Plugins/                        # websocket-sharp.dll（Editor only）
│   └── MobileBridge.Editor.asmdef
├── WebClient/                          # 手机端网页 client.html
├── Documentation~/                     # 文档（不随包导入）
└── Sandbox~/                           # 开发用 Unity 工程（不随包发布）
```

## 工作原理

```
Unity Editor (Game View)
  └── URPCaptureFeature (AfterRendering)
        └── WaitForEndOfFrame → JPEG 编码 → 后台线程发送
                                                    ↓
                                           手机浏览器 (client.html)
                                           Canvas 1:1 渲染画面
                                           Touch 事件 → 归一化坐标
                                                    ↓
                                     Unity TouchReceiver → Input System
```

## 已知限制

### New Input System 路径需要 Game View 聚焦

触控事件通过 Unity Input System 虚拟 `Touchscreen` 设备注入时，Input System 在 Editor 下的默认行为（`PointersAndKeyboardsRespectGameViewFocus`）会在 Game View 失去焦点时过滤所有 `Pointer` 类设备（`Touchscreen` 继承自 `Pointer`）的输入事件——这是 Input System 内部的硬编码逻辑，无法通过插件侧绕过。

**使用方式**：启动 Bridge 后，点击 Game View 窗口使其获得焦点，然后在手机上操作即可正常收到触控。画面串流不受焦点影响，始终正常推送。

> **Legacy 路径无此限制**：若场景使用 `StandaloneInputModule`，插件通过 `BaseInput` override 注入，不经过 Input System 的焦点过滤逻辑，Game View 无需聚焦。

### 仅支持 URP

本插件依赖 `ScriptableRendererFeature` 捕获帧画面，Built-in RP 与 HDRP 不支持此机制。

## FAQ

**Q: 手机打开链接后看不到画面？**
A: 确认 Unity 已进入 Play Mode，且 Mobile Bridge 面板显示「已启动」。检查手机与电脑是否在同一局域网。

**Q: 触控点击位置偏移？**
A: 确保 Game View 没有自定义分辨率黑边缩放以外的变换。Windows 下请检查系统 DPI 缩放是否为整数倍。

**Q: iOS Safari 无法连接？**
A: 本插件使用 `ws://`（明文 WebSocket），iOS 16+ 在某些场景下 Safari 会拦截明文连接。请确认使用局域网 IP 而非 `localhost`，并检查 Safari 的「不安全内容」设置。

**Q: 项目使用 StandaloneInputModule，触控能正常工作吗？**
A: 可以。`StartBridge()` 时插件自动检测场景中的 `StandaloneInputModule`，将 `LegacyTouchInput`（继承 `BaseInput`）设为其 `inputOverride`，UI 点击、拖拽、滚动均正常。注意：直接调用 `Input.GetTouch()` 的 gameplay 代码无法感知桥接的触控（这是 Legacy Input Manager 的底层限制，与本插件无关）。

**Q: 引入包后弹出"enable new input backends"对话框怎么办？**
A: 此对话框不会再出现。Input System 已改为可选依赖，不再强制安装。如果仍出现此提示，说明你的项目此前已单独安装了 Input System 包但尚未配置 backend，与本插件无关。

**Q: 会影响打包后的游戏吗？**
A: 不会。所有网络服务（`WebSocketServer`、`websocket-sharp.dll`）均限定为 Editor Only，不会编译进 Player Build。

## 许可证

MIT License — 详见 [LICENSE](LICENSE)
