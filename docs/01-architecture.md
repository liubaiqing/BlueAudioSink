# 当前架构

BAS2 重构目标与验收约束见 [连接层设计](07-bas2-connection-design.md)。应用入口切换前，本页保留当前 BAS1 行为。

## 模块边界

```text
Android: Compose → LinkController → AudioCaptureService / PcmRecorder → UdpSender
                                                                       ↓ BAS1 / UDP
Windows: MainWindow → WifiController → WifiReceiver → PlayoutBuffer → WASAPI
                   → SinkController → Windows AudioPlaybackConnection / A2DP
                   → TrayIcon（仅 Shell 展示与用户命令）
```

Windows 使用 .NET 8、Win32/GDI 和 NAudio.Wasapi，不引入 Windows Forms/WPF。Android 使用 Kotlin、Compose、AudioPlaybackCapture 和前台服务。

界面框架、控件复用及视觉约束见[双端界面规范](06-ui-style-guide.md)。

- MainWindow 管理导航、偏好操作、模式互斥及窗口生命周期，不负责 PCM 调度。
- SinkController 管理蓝牙连接/重试；EndpointWatcher、RouteControl 管理端点观测及输出偏好。
- WifiController 管理监听器启停、设备候选和只读快照。WifiReceiver 负责收发、来源隔离、重排、会话及诊断。
- PlayoutBuffer 只由 WASAPI Read 消费。正常流不为追逐缓冲目标插零/删样；网络与输出诊断分别计数。
- ConnectionPresentation 将只读快照映射为界面/托盘状态，静音、输出故障和连接成功必须区分。
- TrayIcon 拥有 Shell 图标、菜单和 HICON 缓存；只在窗口线程上调用 Shell API，不持有音频生命周期。

## 生命周期与线程

单实例锁覆盖桌面进程；重复启动恢复现有窗口，包括隐藏到托盘的窗口。启动必须检查原生窗口创建结果，窗口失败不可空转占锁。

模式切换串行释放旧模式，确保无双路出声；Wi-Fi 绑定失败可保留/回退蓝牙。打开设置、隐藏或恢复窗口均不停止音频。彻底退出销毁主窗口、移除托盘图标、停止定时器，并由 Dispose 释放 Wi-Fi、蓝牙和 GDI 资源。

主线程处理消息、绘制及托盘。蓝牙快照通知只 PostMessage 到窗口线程；网络、播放和防火墙工作在后台运行。隐藏期间保持每秒状态检查，以覆盖 Wi-Fi 断流、超时与恢复。Shell 状态未变化不重复修改图标。

## 托盘契约

配置 `closetotray` 默认为 0，旧用户保持关闭即退出。开启后窗口可见时也注册图标；关闭时先确认 Shell 注册成功再隐藏，失败则保留窗口并显示反馈。普通最小化仍进入任务栏。

图标采用统一蓝色音频标识，叠加不同形状/颜色的状态徽标，悬停文字给出模式和具体状态。右键菜单严格只有“打开主界面”“彻底退出”；左键或键盘激活恢复窗口。退出命令直接销毁窗口，不经过关闭拦截。Explorer 重建后通过 TaskbarCreated 消息重新注册；失败时恢复隐藏窗口，后续定时重试。系统注销/关机不拦截为隐藏。

## 持久化与系统集成

设置位于 `%LocalAppData%/BlueAudioSink/settings.ini`；日志在同目录 logs/。修改偏好先重新加载再保存，避免覆盖其他设置更新。防火墙 helper 在单实例/音频初始化之前执行，仅主动点击修改才请求 UAC，规则范围见电脑端规格。正式签名密钥和密码不得进入 Git 或文档。

Wi-Fi 输出由 AudioOutput 管理 WASAPI 共享/独占与软件音量，设备适配层负责端点生命周期；PCM 音量和位深扩展在输出边界处理，不改变 BAS1 网络数据和接收缓冲。蓝牙系统播放不进入此输出链。

可选远程音量控制与PCM通道分离：Android RemoteVolumeClient维护单在途请求、合并及重试，UdpSender复用控制socket；PC WifiVolumeControl维护epoch/修订/确认状态，WifiReceiver校验当前来源与PONG新鲜度，直接更新现有软件增益，MainWindow主线程同步持久化。音量包不能延续音频存活或启动声卡输出

全局快捷键由Platform层GlobalHotkeys负责注册/注销与冲突状态，MainWindow接收WM_HOTKEY并复用既有连接、音量动作。用户录入不使用键盘钩子。Wi-Fi重连只在当前进程记住最近手机，用新接收实例重新探测；手机采集授权不受电脑快捷键控制

Wi-Fi页面选中状态与接收启用状态独立：断开后仍停留Wi-Fi页，所有网络socket、发现定时器和播放器均停止；只能由明确的启用/重连/重试操作恢复接收，UI刷新不能隐式开启。按钮与全局快捷键复用同一控制入口

传输模式记忆与连接启用分离：始终恢复保存的蓝牙/Wi-Fi页面，统一autoconnect控制两种模式的启动和切换启用；主窗口显示后由MainWindow单一入口发起连接，Program不另发蓝牙请求。关于信息来自固定项目元数据与程序集版本，不常驻散落各页
