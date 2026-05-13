# Unity Mobile Bridge — 技术规格文档 SPEC

> 一个 Unity UPM 插件，将 Unity Game View 实时串流至手机浏览器，并将手机触控事件直接注入 Unity Input System，用于在不打包的情况下测试移动端触控交互。
>
> **核心差异点（对比 Unity Remote / Unity Render Streaming）：**
> - 手机端无需安装 App，浏览器直接访问
> - 基于 WebSocket，无需信令服务器，局域网零配置
> - 触控事件直接注入 Unity Input System，系统鼠标完全不动
> - 支持 Mac / Windows 双平台

---

## 一、项目定位与边界

**适用场景：** Unity Editor Play Mode 下的移动端触控测试
**不适用：** 打包后的 Build 测试、非 URP 渲染管线项目

**支持平台：**
- 开发机：macOS、Windows
- 手机端：iOS Safari（需 WSS）、Android Chrome（ws 直连）

---

## 二、系统架构

### 整体数据流

```
┌──────────────────────────────────────────────────────┐
│                    Unity Editor                       │
│                                                       │
│  ┌─────────────────────────────────────────────────┐ │
│  │              Unity Game View                    │ │
│  │  Base Camera                                    │ │
│  │    └── Overlay Camera(s)  ← URP Camera Stack   │ │
│  └──────────────────┬──────────────────────────────┘ │
│                     │ ScriptableRendererFeature       │
│                     │ AfterRendering 捕获             │
│  ┌──────────────────▼──────────────────────────────┐ │
│  │              MobileBridge Runtime                │ │
│  │  ┌─────────────────┐  ┌──────────────────────┐  │ │
│  │  │  FrameCapturer   │  │   TouchReceiver      │  │ │
│  │  │                 │  │                      │  │ │
│  │  │ AsyncGPUReadback│  │ 接收归一化坐标        │  │ │
│  │  │ 后台线程JPEG编码 │  │ 换算 Unity 坐标      │  │ │
│  │  │ WebSocket 推帧   │  │ 注入 Input System    │  │ │
│  │  └────────┬────────┘  └──────────┬───────────┘  │ │
│  └───────────│──────────────────────│───────────────┘ │
└──────────────│──────────────────────│─────────────────┘
               │  画面帧 (JPEG/WSS)   │ 触控坐标 (JSON)
               │         局域网 WiFi  │
               ▼                      │
┌──────────────────────────────────┐  │
│         手机浏览器 client.html    │  │
│                                  │  │
│  Canvas 渲染画面帧                │──┘
│  Touch 事件采集 → 归一化坐标回传  │
└──────────────────────────────────┘
```

### 职责分工

| 模块 | 语言 | 职责 |
|------|------|------|
| `URPCaptureFeature.cs` | C# | 挂载在 URP Renderer Asset，捕获最终合成帧 |
| `FrameCapturer.cs` | C# | AsyncGPUReadback + 后台线程 JPEG 编码 + WebSocket 推流 |
| `TouchReceiver.cs` | C# | WebSocket 服务端接收触控坐标，注入 Unity Input System |
| `CoordinateMapper.cs` | C# | 三坐标系换算，处理 Y 轴翻转和黑边偏移 |
| `MobileBridgeWindow.cs` | C# (Editor) | Editor 控制面板，显示连接状态、二维码、配置参数 |
| `CertificateHelper.cs` | C# (Editor) | 自动生成自签名证书，支持 iOS WSS 连接 |
| `client.html` | HTML/JS | 手机端单文件网页，Canvas 渲染 + Touch 采集 |

---

## 三、UPM 包结构

```
unity-mobile-bridge/                    ← Git 仓库根目录
├── package.json                        ← UPM 包描述文件
├── README.md
├── CHANGELOG.md
│
├── Runtime/                            ← 运行时代码
│   ├── Core/
│   │   ├── MobileBridge.cs             ← 主入口，生命周期管理
│   │   ├── FrameCapturer.cs            ← 画面捕获与推流
│   │   ├── TouchReceiver.cs            ← 触控接收与注入
│   │   └── CoordinateMapper.cs         ← 坐标换算
│   ├── Capture/
│   │   └── URPCaptureFeature.cs        ← ScriptableRendererFeature 实现
│   ├── Network/
│   │   ├── WebSocketServer.cs          ← 基于 System.Net.WebSockets
│   │   └── CertificateHelper.cs        ← 自签证书生成（WSS 用）
│   └── MobileBridge.Runtime.asmdef
│
├── Editor/
│   ├── MobileBridgeWindow.cs           ← Editor 控制面板
│   ├── SetupWizard.cs                  ← 首次配置向导
│   ├── QRCodeGenerator.cs              ← 二维码生成
│   └── MobileBridge.Editor.asmdef      ← Editor Only
│
├── WebClient/
│   └── client.html                     ← 手机端网页（随包分发，运行时 serve）
│
├── Documentation~/
│   └── index.md
│
└── Samples~/
    └── BasicSetup/                     ← 示例场景，含配置好的 URP Renderer Asset
```

### package.json

```json
{
  "name": "com.yourname.unitymobilebridge",
  "version": "0.1.0",
  "displayName": "Unity Mobile Bridge",
  "description": "Stream Unity Game View to mobile browser with touch input support",
  "unity": "2021.3",
  "dependencies": {
    "com.unity.inputsystem": "1.7.0",
    "com.unity.render-pipelines.universal": "14.0.0"
  }
}
```

---

## 四、核心模块详细设计

### 4.1 画面捕获：URPCaptureFeature

**原理：** URP 使用 Camera Stacking，所有 Overlay Camera 的结果最终合成在 Base Camera 的 RenderTarget 中。在 Base Camera 的 `AfterRendering` 阶段捕获即可拿到完整合成画面（含所有 UI 层、特效层）。

```csharp
public class URPCaptureFeature : ScriptableRendererFeature
{
    private CaptureRenderPass _capturePass;

    public override void Create()
    {
        _capturePass = new CaptureRenderPass();
        // 在所有后处理完成后执行，画面已完整
        _capturePass.renderPassEvent = RenderPassEvent.AfterRendering;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer,
                                          ref RenderingData renderingData)
    {
        // 只在以下条件同时满足时注入：
        // 1. 是 Base Camera（非 Overlay）
        // 2. 是 Game View（非 Scene View / Preview 等）
        // 3. MobileBridge 当前处于激活状态
        bool isBaseCamera = renderingData.cameraData.renderType == CameraRenderType.Base;
        bool isGameView   = renderingData.cameraData.cameraType == CameraType.Game;

        if (isBaseCamera && isGameView && MobileBridge.IsActive)
            renderer.EnqueuePass(_capturePass);
    }
}
```

**用户操作：** 在 URP Renderer Asset 的 Renderer Features 列表中添加 `URPCaptureFeature`（Setup Wizard 自动完成）。

---

### 4.2 画面推流：FrameCapturer

**核心流程：**

```
每帧（受 FPS 限制）：

① ScriptableRenderPass.Execute()
     → AsyncGPUReadback.RequestIntoNativeArray(renderTarget, onComplete)
     （非阻塞，主线程立即返回，1-2 帧后回调）

② onComplete 回调（主线程）
     → 将 NativeArray<byte> 数据复制到托管内存
     → 投递到后台线程队列

③ 后台线程（独立 Thread，常驻）
     → 编码 JPEG（使用 System.Drawing 或第三方库）
     → 通过 WebSocket 发送给所有已连接客户端
```

**为什么分三步：**

| 方案 | 问题 |
|------|------|
| `ReadPixels`（同步） | 阻塞主线程等待 GPU，帧率骤降至个位数，不可用 |
| `AsyncGPUReadback` 回调内直接编码 | 回调在主线程，JPEG 编码 10-30ms 仍会卡顿 |
| `AsyncGPUReadback` + 后台线程编码 | 主线程开销 <0.1ms，完全无感知 ✅ |

**帧率限流：**

```csharp
// 不是每帧都捕获，按目标 FPS 限流
if (Time.realtimeSinceStartup - _lastCaptureTime < 1f / targetFPS) return;
_lastCaptureTime = Time.realtimeSinceStartup;
```

**推荐串流分辨率：**

| 分辨率 | JPEG q=75 单帧大小 | 30fps 带宽 | 适用场景 |
|--------|-------------------|-----------|---------|
| 640×360 | ~25 KB | ~6 Mbps | 弱 WiFi 环境 |
| 960×540 | ~50 KB | ~12 Mbps | 推荐默认 |
| 1280×720 | ~80 KB | ~19 Mbps | 强 WiFi 环境 |

串流分辨率独立于 Game View 实际分辨率，捕获后 Resize 再编码。

---

### 4.3 触控注入：TouchReceiver

**接收格式（JSON）：**

```json
{
  "type": "touch",
  "event": "began | moved | ended | cancelled",
  "touches": [
    { "id": 0, "nx": 0.45, "ny": 0.32 },
    { "id": 1, "nx": 0.60, "ny": 0.55 }
  ]
}
```

坐标使用归一化值 `[0,1]`，与手机物理分辨率无关。

**注入 Unity Input System：**

```csharp
void ProcessTouches(TouchData[] touches, TouchPhase phase)
{
    foreach (var t in touches)
    {
        Vector2 unityPos = CoordinateMapper.NormalizedToUnity(t.nx, t.ny);

        var state = new TouchState
        {
            touchId  = t.id,
            phase    = phase,
            position = unityPos,
        };

        // 直接进入 Unity Input System 事件队列
        // 系统鼠标光标完全不动，不影响电脑正常使用
        InputSystem.QueueStateEvent(_touchDevice, state);
    }
}
```

**支持的手势：**

| 手机手势 | 注入事件 |
|---------|---------|
| 单指点击 | TouchPhase.Began + Ended |
| 单指拖拽 | TouchPhase.Began + Moved + Ended |
| 多指触控 | 多个 Touch ID 并发注入 |
| 长按 | Began 持续保持，无 Moved |

---

### 4.4 坐标换算：CoordinateMapper

涉及三个坐标系，需要两次换算：

```
手机触摸坐标          归一化坐标           Unity 屏幕坐标
原点：左上角    →    值域 [0,1]    →    原点：左下角
Y 轴向下            无单位              Y 轴向上
```

**第一次换算（手机端，JS）：**

```javascript
// 必须用 getBoundingClientRect()，不能用 canvas.width
// canvas.width 是像素分辨率，rect.width 是 CSS 显示宽度，两者不同
const rect = canvas.getBoundingClientRect()
const nx = (touch.clientX - rect.left) / rect.width   // [0,1]
const ny = (touch.clientY - rect.top)  / rect.height  // [0,1]
```

**第二次换算（Unity 端，C#）：**

```csharp
public static Vector2 NormalizedToUnity(float nx, float ny)
{
    // 1. 减去黑边偏移（Game View 存在 letterbox 时）
    float effectiveX = nx * _effectiveWidth  + _offsetX;
    float effectiveY = ny * _effectiveHeight + _offsetY;

    // 2. Y 轴翻转：手机原点左上，Unity 原点左下
    float unityX =        effectiveX  * Screen.width;
    float unityY = (1f - effectiveY) * Screen.height;  // ← 最关键的一步

    return new Vector2(unityX, unityY);
}
```

**黑边处理：**

Game View 在非 Stretch 模式下存在黑边（letterbox / pillarbox），必须记录有效区域的偏移量，否则点击坐标会系统性偏移：

```
┌───────────────────────────┐  ← 捕获的完整帧
│  ░░░░░ 黑边(pillarbox) ░░░│
│  ┌─────────────────────┐  │
│  │    有效游戏区域      │  │  ← _effectiveWidth/Height / _offsetX/Y
│  └─────────────────────┘  │
│  ░░░░░ 黑边(pillarbox) ░░░│
└───────────────────────────┘
```

Setup Wizard 首次配置时自动检测并记录偏移量，或引导用户将 Game View 设为 `Scale Mode = Stretch to Window`。

---

### 4.5 网络层：WebSocketServer

Unity 侧同时运行两个服务：

```
端口 8765 (WSS / WS) — WebSocket 主连接
  ├── 服务端 → 客户端：推送 JPEG 画面帧（Binary）
  └── 客户端 → 服务端：接收触控事件（JSON Text）

端口 8766 (HTTPS / HTTP) — 静态文件服务
  └── GET / → 返回 client.html（手机扫码后直接获取页面）
```

基于 `System.Net.WebSockets` 实现，无第三方依赖。

---

## 五、iOS 兼容方案（WSS）

iOS Safari 对 `ws://`（明文）存在限制，需使用 `wss://`（加密连接）。

**自签证书流程（Setup Wizard 自动完成）：**

```
1. 首次启动，CertificateHelper 自动生成本机自签 SSL 证书
2. Editor 面板显示引导：
   「请用手机浏览器访问 https://[IP]:8766/cert，
     按提示安装描述文件（仅需操作一次）」
3. 安装后，wss:// 连接正常工作
4. 证书有效期 365 天，过期后 Setup Wizard 自动重新生成
```

**Android Chrome：** 直接使用 `ws://`，无需证书，零配置。

**自动协议选择（client.html）：**

```javascript
// 根据访问协议自动选择 ws 或 wss，无需用户手动配置
const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'
const ws = new WebSocket(`${protocol}//${location.hostname}:8765`)
```

---

## 六、Editor 控制面板

```
┌──────────────────────────────────────────────┐
│  🟢 Unity Mobile Bridge            v0.1.0   │
├──────────────────────────────────────────────┤
│  状态：已连接  1 台设备                        │
│  延迟：~72ms     FPS：28 / 30               │
├──────────────────────────────────────────────┤
│  串流设置                                     │
│  分辨率：[ 960×540 ▾ ]  质量：[ 75 ──●── ] │
│  目标帧率：[ 30 ▾ ]                          │
├──────────────────────────────────────────────┤
│  连接方式                                     │
│  ┌──────────────────┐                        │
│  │  ██████（二维码） │  192.168.1.5:8766     │
│  │  ██  ██          │                        │
│  │  ██████          │  [ 复制链接 ]          │
│  └──────────────────┘                        │
├──────────────────────────────────────────────┤
│  iOS 证书   [ 已安装 ✓ ]   [ 重新生成 ]      │
│                                              │
│  [ ▶ 启动 ]  [ ⏹ 停止 ]  [ ⚙ Setup Wizard ]│
└──────────────────────────────────────────────┘
```

---

## 七、手机端 client.html

### 页面布局

```
┌─────────────────────────┐
│ 🟢 Connected  28fps     │  ← 状态栏（半透明悬浮）
├─────────────────────────┤
│                         │
│     <canvas>            │  ← 全屏，16:9 比例自适应
│   （touch 事件层）       │
│                         │
└─────────────────────────┘
```

首次打开时若无法自动获取 IP，显示 IP 输入框；之后 localStorage 记住地址，下次自动连接。

### 关键实现

```javascript
// 渲染：避免帧堆积，用 createImageBitmap 异步解码
ws.onmessage = async (e) => {
    const bitmap = await createImageBitmap(new Blob([e.data], { type: 'image/jpeg' }))
    requestAnimationFrame(() => ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height))
}

// 多点触控采集（归一化坐标）
function handleTouch(eventType) {
    return (e) => {
        e.preventDefault()  // 阻止页面滚动、缩放等默认行为
        const rect = canvas.getBoundingClientRect()
        const touches = Array.from(e.changedTouches).map(t => ({
            id: t.identifier,
            nx: (t.clientX - rect.left) / rect.width,
            ny: (t.clientY - rect.top)  / rect.height,
        }))
        ws.send(JSON.stringify({ type: 'touch', event: eventType, touches }))
    }
}

canvas.addEventListener('touchstart',  handleTouch('began'),     { passive: false })
canvas.addEventListener('touchmove',   handleTouch('moved'),     { passive: false })
canvas.addEventListener('touchend',    handleTouch('ended'),     { passive: false })
canvas.addEventListener('touchcancel', handleTouch('cancelled'), { passive: false })
```

---

## 八、性能指标

### 延迟预算（目标：端到端 < 100ms）

```
GPU 渲染完成
  ↓ AsyncGPUReadback 延迟        33-66ms（1-2帧，固定成本）
  ↓ 后台线程 JPEG 编码            5-15ms
  ↓ WebSocket 传输（局域网）       1-5ms
  ↓ 手机浏览器解码渲染             3-8ms
────────────────────────────────────────
总端到端延迟                       约 50-100ms ✅
```

### 对游戏主线程的影响

| 操作 | 执行线程 | 耗时 | 影响 |
|------|---------|------|------|
| AsyncGPUReadback 发起 | 主线程 | <0.1ms | 几乎为零 |
| JPEG 编码 | 后台线程 | 5-15ms | 无影响 |
| WebSocket 发送 | 后台线程 | 1-5ms | 无影响 |
| 触控事件注入 | 主线程 | <0.1ms | 几乎为零 |

**插件对游戏帧率影响可忽略不计。**

---

## 九、用户使用流程

### 首次配置（由 Setup Wizard 引导）

```
1. Package Manager → Add package from git URL
   → https://github.com/yourname/unity-mobile-bridge.git

2. 菜单 Window → Mobile Bridge → Setup Wizard
   ① 自动检测 URP Renderer Asset，添加 URPCaptureFeature
   ② 自动生成自签 SSL 证书
   ③ 引导用户在 iOS 手机上安装证书（Android 跳过此步）

3. 配置完成，后续直接使用 Editor 面板
```

### 日常使用

```
1. Unity Editor 点击 Play
2. 打开 Mobile Bridge 面板，点 [ ▶ 启动 ]
3. 手机扫描二维码 / 访问显示的链接
4. 手机画面出现游戏画面，触控即时生效
```

---

## 十、跨平台说明

| 能力 | macOS | Windows |
|------|-------|---------|
| 画面捕获（AsyncGPUReadback） | ✅ | ✅ |
| WebSocket 服务 | ✅ | ✅ |
| Unity Input System 注入 | ✅ | ✅ |
| 自签证书生成 | ✅ | ✅ |
| DPI 处理 | Retina 固定 2x | 运行时读取系统 DPI 动态换算 |

Windows DPI 换算：

```csharp
#if UNITY_EDITOR_WIN
// 96 是 Windows 基准 DPI（100% 缩放）
// 用户设置 125% 时 Screen.dpi = 120，需要换算
float dpiScale = Screen.dpi / 96f;
// 坐标换算时统一除以 dpiScale
#endif
```

---

## 十一、风险与已知限制

| 风险 | 影响 | 缓解方案 |
|------|------|---------|
| iOS 需安装自签证书 | 首次多一步操作 | Setup Wizard 全程引导，仅需一次 |
| AsyncGPUReadback 固定 1-2 帧延迟 | 延迟无法消除 | 属于方案固有成本，对触控测试可接受 |
| Game View 黑边导致坐标偏移 | 点击位置不准 | 自动检测偏移量，或引导用户开启 Stretch 模式 |
| Windows DPI 各设备不同 | 坐标偏移 | 运行时动态读取系统 DPI，自动换算 |
| iOS Safari 不支持 ws:// | 连接失败 | 强制走 wss://，自签证书方案解决 |
| 仅支持 URP 渲染管线 | Built-in / HDRP 项目无法使用 | SPEC 明确声明范围，文档说明 |

---

## 十二、后续可扩展功能（MVP 范围外）

| 功能 | 说明 |
|------|------|
| 多设备同时连接 | 多台手机并发，模拟多人触控场景 |
| 键盘输入回传 | 手机软键盘注入 Unity Keyboard 设备 |
| 传感器数据 | 陀螺仪 / 加速度计数据回传注入 |
| 自适应码率 | 根据 RTT 延迟自动调整 FPS 和 JPEG 质量 |
| Built-in RP 支持 | 使用 `OnPostRender` 替代 ScriptableRendererFeature |
| 差异帧压缩 | 只传变化区域，进一步降低带宽占用 |
