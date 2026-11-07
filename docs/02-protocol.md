# BAS2：当前 Wi-Fi 协议

双端必须一起升级。BAS1 音频不会回退。协议面向可信局域网，不提供加密或对抗局域网冒充的认证。

## 发现元数据

UDP45678 继续使用原有16字节发现头（magic BAS1），仅复用 OFFER、IDENTITY_QUERY、IDENTITY_REPLY；flags bit7 表示支持 BAS2。其余 BAS1 音频/开始/音量消息不进入正式接收链。旧版元数据用于提示更新，不作为连接确认。

- OFFER(type6)：UTF8电脑名+NUL+小端u32音频端口+32字节小写十六进制电脑ID。
- IDENTITY_QUERY(type11)：16字节随机nonce+32字节ID；全零ID表示初次查询。
- IDENTITY_REPLY(type12)：原nonce+32字节ID+u32音频端口+UTF8电脑名。单包不超过1200字节，名称至多64字符。
- 每接口广播、同子网网关及224.0.0.167（TTL1）。历史地址是有限候选，不扫描整段网段。
- 电脑按入站本地地址/同子网接口回复。发现发送源端口可为临时端口；手机用nonce约束主动查询回复，随后必须通过 TCP 核对 ID。

## 控制通道

手机主动连接 TCP45678。每帧：小端u32长度（1..16384）+UTF8 JSON对象。字段固定为 `type`、非零u32 `requestId`、`sessionId`、对象 `body`。深度最多16。乱序/陈旧请求不执行，最近128个请求缓存结果；同ID不同内容关闭连接。

| 请求/应答 | 语义 |
| --- | --- |
| hello / welcome | protocol=2、expectedDeviceId、instanceId、name；可携带appRunId/attemptId/pathId。应答电脑ID、名称、实际音频端口和随机128位sessionId |
| pathConfirmed / pathReady | 手机经UDP收到挑战回复后，通过TCP提交challenge十六进制；一致才就绪 |
| mediaHello / mediaReady | 独立TCP音频连接使用当前sessionId确认来源及实际两端地址 |
| start / startAck | 电脑分配新非零u64 streamEpoch（JSON中16位十六进制），清音频统计，随后允许发包 |
| stop / stopAck | 停止当前流，拒绝后续媒体，已验证队列有限排空；保留控制连接 |
| heartbeat / heartbeatAck | 每2秒；上报上一轮rttMs；应答phase、audioPackets、audioAgeMs。6秒控制无响应恢复 |
| volumeGet / volumeState | 返回电脑权威epoch、u32 revision、percent、exclusive、accepted |
| volumeSet / volumeState | 提交expected epoch/revision及0..100整数percent；冲突不写入，返回权威值 |
| diagnostics / diagnosticsAck | events至多6条；电脑异步落盘后返回事件ID数组 |
| error | UpgradeRequired、IdentityMismatch、Busy、NotReady、StaleRequest等明确错误；超时不自动归因防火墙 |

只允许一台手机传音。占用时其他来源的开始与调音被拒绝；电脑明确选择手机可关闭旧来源。旧会话的音量请求不会在新连接重放。

## 传输验证

TCP身份确认后，手机向实际UDP音频端口（默认45679）发送16字节随机挑战；电脑从同一端口回复，手机通过控制通道确认。1.5秒未验证则建立独立 TCP45679 音频连接。UDP本地绑定失败不应破坏可用的TCP控制和媒体路径。

连接成功才显示已连接。发送中若持续发送却1秒没有接收证据，控制正常时进行2秒探测，仍无音频确认则新会话转 TCP。本次健康TCP保持至停止，下一次发送重新优先UDP。TCP发送队列上限40帧/200ms，写阻塞300ms关闭音频通道并恢复。明确断开终止重试，其他失败按1/2/4/8/15秒及抖动退避。

## 音频帧

固定48kHz、16bit、双声道、5ms，即240帧/960字节PCM。UDP音频总长1000字节；探测总长56字节。均使用小端40字节头：

| 偏移 | 字节 | 字段 |
| --- | --- | --- |
| 0 | 4 | ASCII BAS2 (0x32534142) |
| 4 | 1 | type：1音频、2挑战、3回复 |
| 5 | 1 | flags：音频仅bit0不连续；探测为0 |
| 6 | 2 | headerSize=40 |
| 8 | 16 | sessionId原始16字节，JSON以32位hex编码，不采用Guid混合端序 |
| 24 | 8 | streamEpoch，音频非零；探测为0 |
| 32 | 4 | sequence，按u32回绕 |
| 36 | 4 | timestampUs，BOOTTIME微秒低32位 |

TCP媒体为小端u32=1000+同一BAS2音频帧。每包校验会话、实际路径、流代际、长度及标志。旧流不进入播放队列。PCM不因传输方式改变格式，UDP不重传；20ms有限重排后缺包补零，真实静音正常发送。

## 诊断与隐私

事件行保持 `[eventId,phoneUnixMs,code,a,b,c,context?]`，ID为32位hex；context为有界对象，记录运行/尝试/路径/会话及系统错误。至多7天、2MiB、4096条，任一达到即轮转。接收确认意味着电脑日志已flush成功，重复交付按eventId识别。首次连接失败可手机导出。

每5秒汇总采集、传输接入、播放与队列统计；手机显示的音频丢包估算来源于接收确认，而非控制心跳丢失。禁止记录音频内容、密码、MAC或VPN节点配置。日志含网络地址时提示分享前检查。
