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
- 手机端：iOS Safari、Android Chrome（统一 `ws://`）

---

## 二、系统架构

### 整体数据流

```
┌──────────────────────────────────────────────────────┐
│                    Unity Editor                       │
│                                                       │
│  ┌─────────────────────────────────────────────────┐ │
│  │              Unity Game View                    │ │
│  │  Base Camera + Overlay Camera(s) + Canvas UI   │ │
│  │    （所有内容在 WaitForEndOfFrame 后完整合成）   │ │
│  └──────────────────┬──────────────────────────────┘ │
│                     │ WaitForEndOfFrame              │
│                     │ ReadPixels（含 Canvas Overlay）│
│  ┌──────────────────▼──────────────────────────────┐ │
│  │              MobileBridge Runtime                │ │
│  │  ┌─────────────────┐  ┌──────────────────────┐  │ │
│  │  │  FrameCapturer   │  │   TouchReceiver      │  │ │
│  │  │                 │  │                      │  │ │
│  │  │ CaptureLoop 协程│  │ 接收归一化坐标        │  │ │
│  │  │ ReadPixels 读帧  │  │ 换算 Unity 坐标      │  │ │
│  │  │ 后台线程推 JPEG  │  │ 注入 Input System    │  │ │
│  │  └────────┬────────┘  └──────────┬───────────┘  │ │
│  └───────────│──────────────────────│───────────────┘ │
└──────────────│──────────────────────│─────────────────┘
               │  画面帧 (JPEG/WS)    │ 触控坐标 (JSON)
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
| `URPCaptureFeature.cs` | C# | 挂载在 URP Renderer Asset，保留空壳以兼容已配置项目（捕获已迁移至 MobileBridge） |
| `FrameCapturer.cs` | C# | 后台线程 JPEG 编码 + WebSocket 推流 |
| `TouchReceiver.cs` | C# | WebSocket 服务端接收触控坐标，注入 Unity Input System |
| `CoordinateMapper.cs` | C# | 三坐标系换算，处理 Y 轴翻转和黑边偏移 |
| `MobileBridgeWindow.cs` | C# (Editor) | Editor 控制面板，显示连接状态、二维码、配置参数 |
| `client.html` | HTML/JS | 手机端单文件网页，Canvas 渲染 + Touch 采集 |

---

## 三、UPM 包结构

```
unity-mobile-bridge/                    ← Git 仓库根目录
├── package.json                        ← UPM 包描述文件
├── README.md
├── CHANGELOG.md
│
├── Runtime/                            ← 运行时代码（会进入玩家构建包）
│   ├── Core/
│   │   ├── MobileBridge.cs             ← 主入口，生命周期管理
│   │   ├── FrameCapturer.cs            ← 画面捕获与推流
│   │   ├── TouchReceiver.cs            ← 触控接收与注入
│   │   └── CoordinateMapper.cs         ← 坐标换算
│   ├── Capture/
│   │   └── URPCaptureFeature.cs        ← ScriptableRendererFeature 实现
│   ├── Network/
│   │   └── WebSocketServer.cs          ← 基于 websocket-sharp
│   ├── Plugins/
│   │   └── websocket-sharp.dll         ← Runtime 依赖
│   └── MobileBridge.Runtime.asmdef
│
├── Editor/                             ← Editor Only（不进入玩家构建包）
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

### 4.1 画面捕获：MobileBridge.CaptureLoop

**原理：** Unity 的 `WaitForEndOfFrame` 是唯一能看到完整合成帧（含 Screen Space Overlay Canvas）的时机。`ScriptableRenderPass.Execute()` 在 URP 渲染管线内执行，此时 Canvas Overlay 尚未叠加到屏幕；`WaitForEndOfFrame` 则等待 Unity 将所有内容（3D 场景 + Overlay Camera + Canvas UI）完整合成到 backbuffer 之后，才继续执行。

**为什么不用 URPCaptureFeature + AsyncGPUReadback：**

| 方案 | Canvas Overlay 可见 | 说明 |
|------|-------------------|------|
| `ScriptableRenderPass AfterRendering` + AsyncGPUReadback | ❌ | 捕获时机在 Canvas Overlay 合成之前 |
| `WaitForEndOfFrame` + `ReadPixels` | ✅ | Unity 已将所有内容合成到 backbuffer |

`URPCaptureFeature` 保留为空壳，防止已配置 Renderer Asset 的项目报引用丢失错误，不再注入任何 RenderPass。

**CaptureLoop 伪代码（在 `MobileBridge.cs` 中）：**

```csharp
private IEnumerator CaptureLoop()
{
    var waitEof = new WaitForEndOfFrame();
    while (IsActive)
    {
        // 按 targetFps 限流
        if (Time.realtimeSinceStartup - _lastCaptureTime < 1f / targetFps)
        {
            yield return null;
            continue;
        }

        yield return waitEof;   // ← 此处 Canvas Overlay 已完整合成

        // ReadPixels 从屏幕 backbuffer 读取像素（同步，~1-3ms）
        _readbackTex.ReadPixels(new Rect(srcX, srcY, readW, readH), 0, 0, false);
        _readbackTex.Apply(false);

        byte[] jpeg = _readbackTex.EncodeToJPG(jpegQuality);
        _capturer.EnqueueJpeg(jpeg);   // 投入后台线程队列发送
    }
}
```

**用户操作：** `URPCaptureFeature` 仍需挂载在 URP Renderer Asset（Setup Wizard 自动完成），但其存在不影响捕获行为。

---

### 4.2 画面推流：FrameCapturer

**核心流程：**

```
每帧（受 targetFps 限流）：

① MobileBridge.CaptureLoop（协程，主线程）
     yield return WaitForEndOfFrame
     → ReadPixels 读取完整屏幕（含 Canvas Overlay）
     → EncodeToJPG（主线程，~2-5ms）
     → EnqueueJpeg() 投入后台队列

② FrameCapturer 后台线程（常驻）
     → 取出 JPEG bytes
     → WebSocket BroadcastFrameAsync 发送给所有客户端
```

**为什么 ReadPixels 在主线程执行是可接受的：**

| 操作 | 执行线程 | 耗时 | 说明 |
|------|---------|------|------|
| `ReadPixels`（同步读屏） | 主线程 | ~1-3ms | GPU 同步，轻微开销，Editor 工具可接受 |
| `EncodeToJPG` | 主线程 | ~2-5ms | Unity 内置编码器，无外部依赖 |
| WebSocket 发送 | 后台线程 | 1-5ms | 不影响主线程 |

> **注意：** 与原三段式方案相比，ReadPixels 会引入轻微主线程 GPU 同步开销（约 1-3ms），但消除了 AsyncGPUReadback 固有的 1-2 帧异步延迟，且能正确捕获 Canvas Overlay。对 Editor 调试工具而言这是合理的权衡。

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
端口 8765 (WS) — WebSocket 主连接
  ├── 服务端 → 客户端：推送 JPEG 画面帧（Binary）
  └── 客户端 → 服务端：接收触控事件（JSON Text）

端口 8766 (HTTP) — 静态文件服务
  └── GET / → 返回 client.html（手机扫码后直接获取页面）
```

基于 `websocket-sharp` 实现。

---

## 五、iOS 与 Android 连接策略（统一 WS）

iOS Safari 与 Android Chrome 均使用明文局域网链路：

- 页面访问：`http://[IP]:8766`
- WebSocket 连接：`ws://[IP]:8765`

不再包含证书生成、下载、安装与信任流程。

**客户端连接逻辑（client.html）：**

```javascript
const ws = new WebSocket(`ws://${location.hostname}:8765`)
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

### 延迟预算（目标：端到端 < 120ms）

```
GPU 渲染完成
  ↓ WaitForEndOfFrame 等待                 0-33ms（等待本帧渲染完成）
  ↓ ReadPixels（同步读屏）                  1-3ms
  ↓ EncodeToJPG（主线程）                   2-5ms
  ↓ WebSocket 传输（局域网）                 1-5ms
  ↓ 手机浏览器解码渲染                       3-8ms
────────────────────────────────────────
总端到端延迟                               约 10-55ms ✅
```

> 与 AsyncGPUReadback 方案相比，消除了 1-2 帧（33-66ms）的 GPU 异步等待，但引入约 3-8ms 主线程同步开销。整体延迟更低，且支持 Canvas Overlay。

### 对游戏主线程的影响

| 操作 | 执行线程 | 耗时 | 影响 |
|------|---------|------|------|
| WaitForEndOfFrame 等待 | 协程（主线程） | 0ms（等待，不占 CPU） | 无影响 |
| ReadPixels | 主线程 | 1-3ms | 轻微 GPU 同步开销 |
| EncodeToJPG | 主线程 | 2-5ms | 轻微，Editor 工具可接受 |
| WebSocket 发送 | 后台线程 | 1-5ms | 无影响 |
| 触控事件注入 | 主线程 | <0.1ms | 几乎为零 |

**插件对游戏帧率影响极小（主线程总计约 3-8ms/帧），对 Editor 调试工具可接受。**

---

## 九、用户使用流程

### 首次配置（由 Setup Wizard 引导）

```
1. Package Manager → Add package from git URL
   → https://github.com/yourname/unity-mobile-bridge.git

2. 菜单 Window → Mobile Bridge → Setup Wizard
   ① 自动检测 URP Renderer Asset，添加 URPCaptureFeature

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
| ReadPixels 主线程 GPU 同步 | 每帧额外 1-3ms 主线程开销 | Editor 调试工具可接受；如需优化可探索 AsyncGPUReadback + 额外合成步骤 |
| Game View 黑边导致坐标偏移 | 点击位置不准 | 自动检测偏移量，或引导用户开启 Stretch 模式 |
| Windows DPI 各设备不同 | 坐标偏移 | 运行时动态读取系统 DPI，自动换算 |
| 企业/公共网络限制明文 WS | 连接失败或握手被拦截 | 建议使用同一私有 Wi-Fi，避免隔离网络/访客网络 |
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
