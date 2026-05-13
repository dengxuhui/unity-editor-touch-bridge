# P1 阶段开发计划 — 基础链路打通（iOS Safari）

> **执行基准**：本文档是 P1 阶段的 agent 开发参考，所有任务按此执行。
> 完成后同步更新 `DEVELOPMENT_PLAN.md` 的阶段状态与变更记录。

## P2 阶段对本文档的修订说明（2026-05-13）

P1 计划编写时尚未发现 Unity 2022.3 Mono 的 `System.Security.Cryptography` 兼容性问题，
P2 阶段进行了三轮修复，对以下任务的实现有实质性偏差，特此记录：

### T2 — SAN IP 构建（已修订）

- **原计划**：在 `CertificateHelper`（Runtime）中新增 `GetLocalIPAddress()` 并在 `Generate()` 里使用。
- **实际实现**：
  - `GetLocalIPAddress()` 保留在 `CertificateHelper`（Runtime），作为公共工具方法供 Editor 调用，避免重复实现。
  - `Generate()` 方法整体迁移至 `Editor/CertificateGenerator.cs`（Editor-only asmdef），Runtime 中不含任何生成逻辑。
  - SAN 构建使用 BouncyCastle `GeneralNames` API，不使用 `SubjectAlternativeNameBuilder`（Mono stub，抛 `PlatformNotSupportedException`）。
  - 原计划提到的 `san.AddDnsName()` / `san.AddIpAddress()` 不可用；改用 `new GeneralName(GeneralName.IPAddress, new DerOctetString(...))` 原始 ASN.1 方式。

### T3 — 证书存储格式（已修订）

- **原计划**：使用 PFX（PKCS#12）存储，`/cert` 端点通过 `cert.Export(X509ContentType.Cert)` 导出 DER。
- **实际实现**：改用 **两文件存储**：
  - `cert.pem`：X.509 DER 编码，base64 包裹（PEM 格式）
  - `key-params.xml`：RSA 私钥，XML 格式（`RSACryptoServiceProvider.ToXmlString(true)`）
  - **原因**：BouncyCastle 生成的 PKCS#12 使用 RC2-40-CBC 加密算法，Mono 无法解析，`new X509Certificate2(pfxBytes, password)` 抛 `CryptographicException`；`ImportPkcs8PrivateKey` / `ImportRSAPrivateKey` 也是 stub。
  - `/cert` 端点实现不变（`cert.Export(X509ContentType.Cert)` 输出 DER，仅含公钥）；加载流程改为 `DecodePem()` + `new X509Certificate2(der)` + `RSACryptoServiceProvider.FromXmlString(xml)` + `CopyWithPrivateKey(rsa)`。
  - `CertificateHelper.LoadOrCreate()` 在计划中提及，但实际未实现此方法；Editor 侧统一调用 `CertificateGenerator.Generate()`，Runtime 侧调用 `CertificateHelper.Load()`，两者职责明确分离。

### URPCaptureFeature — SetupRenderPasses 修复（P2 补充，T4 范畴）

- `cameraColorTargetHandle` 从 `AddRenderPasses` 移至 `SetupRenderPasses` 重写，符合 URP 2022.3 明确要求。
- `in RenderingData` 参数不可用 `ref var` 取引用（CS8330），改为直接访问字段。

---

## 目标

完成「扫码打开页面 → 看到画面 → 触控回传」的最小闭环，以 **iOS Safari + wss://** 为验收环境。

## 环境约束

- 开发机：macOS
- 测试设备：iOS Safari（强制 wss://，不支持 ws://）
- WebSocket 库：websocket-sharp（预编译 dll，方式 B）
- Unity：2022.3 LTS，URP 14.0+

---

## 任务清单

### T1 — 引入 websocket-sharp.dll

**文件**：`Runtime/Plugins/websocket-sharp.dll`（新增）、`Runtime/MobileBridge.Runtime.asmdef`

**步骤**：
1. 克隆 `https://github.com/sta/websocket-sharp`，切到 `master` 分支
2. 用 `dotnet build` 编译，目标框架指定 `netstandard2.0`：
   ```
   <TargetFramework>netstandard2.0</TargetFramework>
   ```
3. 将生成的 `websocket-sharp.dll` 放入 `Runtime/Plugins/websocket-sharp.dll`
4. 在 Unity 中为该 dll 创建正确的 `.meta`（Platform: Any Platform，Editor 勾选）
5. 在 `Runtime/MobileBridge.Runtime.asmdef` 的 `precompiledReferences` 中加入 `"websocket-sharp.dll"`

**验收**：Unity 编译无报错，`using WebSocketSharp;` 可正常引用。

---

### T2 — CertificateHelper 补充局域网 IP 到 SAN

**文件**：`Runtime/Network/CertificateHelper.cs`

**问题**：当前 SAN 只含 `localhost`，iOS Safari 访问局域网 IP（如 `192.168.1.5`）时证书 SAN 不匹配，即使已安装证书也会被拒绝。

**具体改动**：

1. 新增静态方法 `GetLocalIPAddress()`，通过 UDP connect trick 获取局域网出口 IP：
   ```csharp
   private static string GetLocalIPAddress()
   {
       try
       {
           using var sock = new System.Net.Sockets.Socket(
               System.Net.Sockets.AddressFamily.InterNetwork,
               System.Net.Sockets.SocketType.Dgram, 0);
           sock.Connect("8.8.8.8", 65530);
           return ((System.Net.IPEndPoint)sock.LocalEndPoint).Address.ToString();
       }
       catch { return null; }
   }
   ```

2. 在 `Generate()` 的 SAN 构建中补充 IP：
   ```csharp
   san.AddDnsName("localhost");
   san.AddIpAddress(System.Net.IPAddress.Loopback);   // 127.0.0.1

   var localIp = GetLocalIPAddress();
   if (localIp != null)
       san.AddIpAddress(System.Net.IPAddress.Parse(localIp));
   else
       Debug.LogWarning("[MobileBridge] Could not detect local IP; certificate SAN will not include LAN IP.");
   ```

**验收**：生成的证书 SAN 包含局域网 IP，可通过 `openssl x509 -in cert.cer -text` 确认。

---

### T3 — 重写 WebSocketServer.cs（基于 websocket-sharp）

**文件**：`Runtime/Network/WebSocketServer.cs`（全部重写）

**对外 API 保持不变**：
- `Start()` / `Stop()`
- `Task BroadcastFrameAsync(byte[] jpegBytes)`
- `event Action<string> OnTouchMessage`
- `int ClientCount`
- 常量 `WsPort = 8765`、`HttpPort = 8766`

**新实现架构**：

```
WebSocketServer（端口 8765，SSL=true）
  └── BridgeBehavior : WebSocketBehavior
        ├── OnOpen()    → 无需维护客户端列表，websocket-sharp 内部管理
        ├── OnClose()   → 日志
        └── OnMessage() → Text 类型 → 触发 OnTouchMessage

HttpServer（端口 8766，SSL=true）
  └── OnGet 处理：
        "/"      → 返回 client.html（text/html）
        "/cert"  → 从 PFX 导出 DER 格式 .cer，返回（application/x-x509-ca-cert）
        其他     → 404
```

**SSL 配置**（两个服务相同）：
```csharp
var cert = CertificateHelper.LoadOrCreate();
server.SslConfiguration.ServerCertificate = cert;
server.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
server.SslConfiguration.ClientCertificateValidationCallback = (_, __, ___, ____) => true;
```

**BroadcastFrameAsync 实现**：
```csharp
public Task BroadcastFrameAsync(byte[] jpegBytes)
{
    _wsServer?.WebSocketServices["/"]?.Sessions.Broadcast(jpegBytes);
    return Task.CompletedTask;
}
```

**`/cert` 端点**：从 PFX 中导出公钥 DER 字节（`.cer`）：
```csharp
var cert = CertificateHelper.LoadOrCreate();
byte[] derBytes = cert.Export(X509ContentType.Cert);   // DER 格式，无私钥
// Content-Type: application/x-x509-ca-cert
// Content-Disposition: attachment; filename="mobileBridge.cer"
```

**ClientCount 实现**：
```csharp
public int ClientCount =>
    _wsServer?.WebSocketServices["/"]?.Sessions.Count ?? 0;
```

**Stop 实现**：
```csharp
public void Stop()
{
    _wsServer?.Stop();
    _httpServer?.Stop();
}
```

**注意**：
- `WebSocketServer` 路径设为 `"/"` 即可，客户端连接 `wss://IP:8765/`
- `HttpServer` 不使用 websocket-sharp 的 WS 功能，只用其 HTTP 层
- `BridgeBehavior` 需要能访问外部的 `OnTouchMessage` 事件，通过静态委托或构造注入传入

**验收**：
- `wss://[IP]:8765` 可建立 WebSocket 连接
- `https://[IP]:8766/` 返回 client.html
- `https://[IP]:8766/cert` 返回 `.cer` 文件（Safari 弹出安装提示）

---

### T4 — URPCaptureFeature + MobileBridge 生命周期健壮化

**文件**：`Runtime/Capture/URPCaptureFeature.cs`、`Runtime/Core/MobileBridge.cs`

**问题**：退出 Play Mode 时 `AsyncGPUReadback` 回调可能在 `MobileBridge` 销毁后 1-2 帧触发，引发 NullRef。

**MobileBridge.cs 改动**：
- 确认 `OnDestroy()` 中 `StopBridge()` 最先将 `IsActive = false`（当前 `StopBridge` 第一行已是 `IsActive = false`，确认顺序正确即可）
- `OnFrameReady()` 已有 `if (!IsActive || _capturer == null) return;` 保护，确认无误

**URPCaptureFeature.cs 改动**：
- `Create()` 中 `_pass` 赋值后增加 null 检查保护
- `AddRenderPasses()` 中增加 `_pass != null` 守卫：
  ```csharp
  if (_pass != null && isBase && isGameView && MobileBridge.IsActive)
  ```
- 回调中的保护已有（`req.hasError || !MobileBridge.IsActive`），确认覆盖退出场景

**验收**：反复进入/退出 Play Mode 5 次，Unity Console 无任何 NullReferenceException。

---

### T5 — MobileBridgeWindow 更新：HTTPS URL + 证书状态 + iOS 引导

**文件**：`Editor/MobileBridgeWindow.cs`

**改动点**：

1. **URL 改为 `https://`**（第 128 行）：
   ```csharp
   string url = $"https://{ip}:{WebSocketServer.HttpPort}";
   ```

2. **新增 `DrawCertificate()` 方法**，在 `DrawConnection()` 之后、`DrawControls()` 之前调用：
   ```
   ── iOS Certificate ──────────────────────────────────
   Status: [Valid until 2027-05-13] 或 [Not generated]
   Install: https://192.168.1.5:8766/cert   [Copy]
   ℹ Open this URL in Safari → install the profile →
     trust in Settings > General > VPN & Device Management
   [Generate Certificate] 或 [Regenerate]
   ─────────────────────────────────────────────────────
   ```

3. **StartBridge() 中自动检查证书**：
   ```csharp
   private void StartBridge()
   {
       if (CertificateHelper.NeedsRenewal())
           CertificateHelper.LoadOrCreate();   // 自动生成/续签
       EnsureBridgeComponent();
       // ... 其余不变
   }
   ```

**验收**：面板显示证书有效期，复制 Install URL 后 Safari 可访问并弹出安装提示。

---

### T6 — client.html 补充 WSS 连接失败提示

**文件**：`WebClient/client.html`

**改动**：在 `ws.onerror` 和 `ws.onclose` 回调中，若协议为 `wss://` 且尚未成功连接过，在状态栏追加提示文字：

```javascript
let _everConnected = false;

ws.onopen = () => {
    _everConnected = true;
    // ... 现有逻辑
};

ws.onerror = (e) => {
    if (!_everConnected && location.protocol === 'https:') {
        setStatus('Connection failed. Please install the certificate first: ' +
                  location.protocol + '//' + location.hostname + ':' +
                  wsPort.toString().replace('8765','8766') + '/cert');  // 提示安装链接
    }
};
```

**注意**：提示文字需简洁，下次成功连接（`ws.onopen`）后重置状态栏。

**验收**：未安装证书时打开页面，状态栏显示证书安装引导链接。

---

## 执行顺序

```
T4（独立，先做排除干扰）
  ↓
T1（引入 dll）
  ↓
T2（SAN IP，依赖 T1 编译通过）
  ↓
T3（重写 WebSocketServer，依赖 T1 + T2）
  ↓
T5（Editor 面板，依赖 T3 的新 API）
T6（client.html，依赖 T3 的 /cert 端点）
```

---

## iOS 验收检查清单

| # | 检查项 | 通过条件 |
|---|--------|---------|
| 1 | 证书生成 | `CertPath` 文件存在，SAN 含局域网 IP |
| 2 | 证书下载 | Safari 访问 `https://[IP]:8766/cert` 弹出安装提示 |
| 3 | 证书安装 | 设置 > 通用 > VPN与设备管理 中可信任该证书 |
| 4 | 页面加载 | `https://[IP]:8766` 无证书错误，页面正常渲染 |
| 5 | WSS 连接 | 状态栏显示 `Connected`，无 console 报错 |
| 6 | 画面推流 | canvas 显示 Unity Game View 内容，帧持续刷新 |
| 7 | 触控回传 | 点击 canvas，Unity 中 Input System 有 touch 事件 |
| 8 | Play Mode 退出 | Console 无 NullRef 或未捕获 Exception |
| 9 | 重连 | 关闭再打开 Safari 标签，画面自动恢复 |

---

## 完成后需同步

1. 更新 `Documentation~/DEVELOPMENT_PLAN.md`：
   - P1 状态改为 `DONE`，填写完成时间
   - 变更记录中追加各任务说明
   - 备注"iOS Safari wss:// 验收通过；Android ws:// 留 P2 补充验证"
2. 更新 `CHANGELOG.md` 记录 P1 主要变更
