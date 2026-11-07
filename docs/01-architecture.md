# 当前架构

双端正式 Wi-Fi 入口使用 BAS2；蓝牙仍使用系统 A2DP。协议见[线格式](02-protocol.md)，连接约束见[连接层实现](07-bas2-connection-design.md)，视觉约束见[双端界面规范](06-ui-style-guide.md)。

## 模块边界

```text
Android: Compose → LinkController → Bas2Sender
             AudioCaptureService/PcmRecorder → SessionCoordinator → ControlConnection → UDP / TCP
             NetworkEnvironment → LanDiscovery / RoutePlanner / RouteRace
Windows: MainWindow → WifiController → WifiReceiverFacade → DiscoveryServer
                                           → PlaybackReceiver → ControlServer
                                                  ↓ verified PCM
                                      WifiReceiver(playback-only) → PlayoutBuffer → AudioOutput/WASAPI
           SinkController → Windows AudioPlaybackConnection / A2DP
```

- Windows：.NET 8、Win32/GDI、NAudio.Wasapi，不引入 WinForms/WPF。
- Android：Kotlin、Compose、AudioPlaybackCapture、mediaProjection 前台服务，不依赖 root、Shizuku 或调试授权。
- 身份、物理路径、控制会话和音频流相互独立。电脑身份沿用原身份文件；广播只提供线索，TCP 核对才确认身份。
- Android SessionCoordinator 的串行执行器决定连接状态。最多3条路径并行探测，胜出后关闭其余资源；迟到结果校验目标及连接尝试代际。
- ControlServer 用会话门串行裁决来源、请求和流代际，网络读写异步；诊断落盘不持有会话门。
- PlaybackReceiver 的 socket 回调只投递到40帧有界队列；独立接入线程调用不绑定网络的现有播放引擎。UDP/TCP 共享重排、缓冲、软件增益和输出逻辑。
- PlayoutBuffer 只由输出消费者读取。正常流不追逐缓冲目标插零/删样；接入丢弃、迟到、缺包、真实输出欠载分开记录。
- UI 只读取快照；进入设置或托盘不改变连接。连接、采集、静音、缓冲和输出故障分别判断。

## 生命周期

单实例锁覆盖桌面进程，重复启动恢复原窗口。模式切换释放旧模式，两路不同时播放。Wi-Fi 显式断开停止发现、TCP/UDP、恢复与输出；页面刷新不能隐式开启。电脑重连重新监听，手机控制连接失效后主动寻址；只有原授权仍有效时才自动恢复采集发送。

正常停止发送先关闭发送闸门，通过控制通道停止流。电脑先处理已经验证的接入队列，再有限排空；断开或新流则丢弃旧队列。会话、接口与输出代际检查阻止迟到任务覆盖新状态。

主线程仅处理原生窗口、绘制与托盘。网络、诊断和播放在后台；跨线程 UI 操作只投递消息。彻底退出移除托盘、注销快捷键、停止定时器并释放所有所属资源。关闭时缩小到托盘默认关闭；右键只保留打开和彻底退出。

## 偏好和系统集成

设置位于 `%LocalAppData%/BlueAudioSink/settings.ini`。默认日志目录为 EXE 旁 `logs` 子目录，用户可修改位置与保留天数，默认90天。手机最近7天/2MiB/4096条诊断通过控制通道补传，电脑落盘后确认。分享日志前检查地址和名称。

电脑是音量权威；手机 SET 携带 epoch/revision，冲突返回当前值，重连不补发旧操作。独占切换先静音；共享模式仍受系统合成器影响。蓝牙不经过这套 PCM 输出。

防火墙只在用户点击后请求 UAC，为当前 EXE、LocalSubnet、Private/Public 的必要 TCP/UDP 端口生成两条规则。旧版规则检测为需更新，不自动提权。禁止关闭系统防火墙、自动修改网络类别或承诺突破 VPN 的系统策略。

## 演进边界

不提供互联网中继、多手机混音、加密或强制捕获受保护应用。异步采样率校正、编码协商和应用自动更新未实现。新增功能先修改架构/协议及验收文档；命令输出、网络地址和历史测试结果只进入忽略的 artifacts。
