# 音频回归与录音工具

直接链接产品源码，验证播放缓冲内容保真与恢复机制，不启动产品窗口；网络集成项临时绑定系统分配的 UDP 端口，不占用产品的 45678/45679 端口。

```powershell
dotnet run --project tools/AudioRegression/AudioRegression.csproj -c Release
```

10 项音频回归包括：健康 PCM 保真、真实补零计数、恢复阈值、正常排空、空暂存队列不误欠载、重复 START 的消费者隔离、乱序、真正缺包截止、序号回绕、90 秒双节拍连续流。另有 5 项 UI 状态映射回归，覆盖静音、过期端点、断流、恢复/排空/输出失败和启动故障，再加 4 项多设备发现、来源锁定与真实双 UDP 发送端切换测试，再加 2 项双模式托盘状态映射与 1 项原生 Shell 托盘生命周期检查，共 22 项。原生项使用独立隐藏测试窗口，临时注册并移除自己的托盘图标，覆盖全部状态和重复启停后的 USER 句柄释放；需要已登录的交互桌面。

录制 Windows 默认输出端点（标准 WAV，真实采样格式写入文件头；JSON 输出设备和格式）：

```powershell
dotnet run --project tools/AudioRegression/AudioRegression.csproj -c Release -- --record 60 C:\Users\Public\received.wav
```

将原 MP3 解码成 48 kHz / 16-bit / stereo 标准 WAV，供匹配同一音乐片段使用：

```powershell
dotnet run --project tools/AudioRegression/AudioRegression.csproj -c Release -- --decode 'D:\music.mp3' 'C:\Users\Public\reference.wav'
```

不要以自然音乐的峰值/全曲 RMS 分位数直接判定音频是否压缩；先匹配片段、增益和时间轴。Read 计数只覆盖应用主动补零，仍应结合输出录音和输入最长零帧段，区分采集端与系统音频栈的异常。

检查标准 WAV 中的内部近零片段（需要 numpy/scipy）：

```powershell
python tools/AudioRegression/analyze_recording.py C:\Users\Public\received.wav
```

## WASAPI 输出测试

现有 22 项、13 项输出、7 项远程音量、7 项快捷键/重连与 2 项启用/断开测试共 51 项（含双缓冲启动、取消和有限流排空）。输出项使用隔离设备/播放器接口验证共享/独占状态、失败回退、静音保护、增益平滑、位深扩展及释放，不修改系统音量或用户配置。

`--exclusive-probe [endpointId]` 执行软件静音的真实设备独占/冲突/释放测试。`--output-flow [endpointId]` 绑定产品端口，等待真实手机后静音切换共享→独占→共享并校验快照。两者会短暂占用目标输出端点，需退出接收程序后运行；此项不验证听感或音质。

`--wasapi-cadence <48kHz-16bit-stereo.pcm> [endpointId]` 以相同 PCM、65% 软件音量对比共享事件50ms、独占轮询50ms、独占事件50/100ms，记录供数间隔和送入 WASAPI 前的 PCM 哈希

`--output-benchmark <48kHz-16bit-stereo.pcm> [endpointId]` 以相同 PCM、相同160ms输入预填、5ms实时供包和65%音量对比完整产品输出链：共享事件、独占轮询参考、修复后的独占事件。参考项仅替换设备供数策略，仍使用当前产品的启动保护，不等同于旧版完整重现

两项会实际播放音频并短暂独占端点，需先退出产品，报告位于 artifacts/exclusive-noise。均未录制声卡/DAC输出，供数哈希一致与缺数为0不能替代听感验收。事件间隔略大于设备周期不直接计为声卡故障；cadence结果中的 gapsAboveBuffer 不是爆音次数。不得用共享回环录音冒充独占输出录音

`--session-status` 只读列出默认输出、各播放端点的音量/静音状态，以及音频会话的进程、活动状态和短时峰值，用于区分独占未释放与应用仍向其他设备供音；不更改系统或应用音量

远程音量测试覆盖读取不写入、重复幂等、独占静音拒绝陈旧调高、范围/来源/token/请求顺序校验、心跳门控、真实UDP查询→设值→电脑主动通知。Android :app:testReleaseUnitTest 覆盖客户端合并/最终值、同ID重试、冲突取消、首次读取及断线清空意图

快捷键测试使用隔离注册后端验证默认未绑定、格式往返、内部重复、冲突原样保留且不寻找替代组合、启动不可用、清除/释放和旧消息过滤；另以隐藏原生窗口核对真实RegisterHotKey冲突/释放，并以UDP手机验证重连只探测记住的来源、不打开声卡、不被其他来源抢占

启用/断开项验证关闭状态不使用旧心跳或音频冒充连接、启用只等待响应，以及控制/音频socket同时关闭、发现不可发送、端口可重新绑定、显式启用可重新监听
