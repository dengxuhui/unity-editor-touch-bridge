# Unity Editor Touch Bridge

一个 Unity UPM 插件，将 Unity Game View 实时串流至手机浏览器，并将手机触控事件直接注入 Unity Input System，用于在不打包的情况下测试移动端触控交互。

## 核心特性

- **手机端无需安装 App**，用浏览器直接访问即可
- **基于 WebSocket**，局域网零配置，无需信令服务器
- **触控事件直接注入 Unity Input System**，系统鼠标完全不动
- **支持 Mac / Windows** 双平台开发机
- **支持 iOS Safari（WSS）和 Android Chrome（WS）**

## 系统要求

- Unity 2022.3 LTS 或更高版本
- Universal Render Pipeline (URP) 14.0 或更高版本
- Unity Input System 1.7.0 或更高版本

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
3. iOS 用户按引导安装自签证书（仅需一次）
4. 点击 Play，打开 Mobile Bridge 面板，点击 **启动**
5. 手机扫描二维码或访问显示的链接，即可看到游戏画面并进行触控操作

## 项目结构

```
unity-editor-touch-bridge/
├── package.json                        # UPM 包描述
├── Runtime/                            # 运行时代码
│   ├── Core/                           # 核心逻辑（MobileBridge, FrameCapturer 等）
│   ├── Capture/                        # URP ScriptableRendererFeature
│   ├── Network/                        # WebSocket 服务器 & 证书工具
│   └── MobileBridge.Runtime.asmdef
├── Editor/                             # 编辑器面板、Setup Wizard、二维码生成
│   └── MobileBridge.Editor.asmdef
├── WebClient/                          # 手机端网页 client.html
├── Documentation~/                     # 文档
├── Samples~/                           # 示例场景
└── Sandbox/                            # 开发用 Unity 工程（不随包发布）
```

## 工作原理

```
Unity Editor (Game View)
  └── URPCaptureFeature (AfterRendering)
        └── AsyncGPUReadback → 后台线程 JPEG 编码 → WebSocket 推流
                                                            ↓
                                                   手机浏览器 (client.html)
                                                   Canvas 渲染画面
                                                   Touch 事件 → 归一化坐标
                                                            ↓
                                             Unity TouchReceiver → Input System
```

## 许可证

[LICENSE](LICENSE)
