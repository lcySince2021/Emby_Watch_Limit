# Watch Time Limit（Emby 每日观看时长限制插件）

适用于 Emby Server 4.10（针对 4.10.0.10 的 DLL 编译，目标框架 netstandard2.0）。

## 功能
- 统计每个用户每天的播放时长（每 15 秒采样一次；同一用户多个设备同时播放只按真实时间计一次；暂停时是否计时可以设置）
- 超过上限：先给客户端发提示并停止播放，再禁用账户（可选：吊销该用户所有登录令牌，强制登出）
- 剩余 N 分钟、剩余 1 分钟各提醒一次；开始播放时提示今日剩余时长（同一用户 10 分钟内最多一次）
- 超时时如果正在播放，允许看完当前内容（最多 N 分钟，默认 10，可设为 0 立即停止）；切换内容或到时间后才停止并禁用
- 每天在设定的整点自动重置，被插件禁用的账户自动恢复
- 可选“受限媒体库”：只有播放这些库的内容才计时（不选 = 全部计时）
- 超时处理方式：禁用账户（默认），或“只停用受限媒体库”——账户保持可用，其他库照常观看；插件会临时把受限库从该用户的媒体库权限里移除（重置/加时/豁免/第二天自动加回），所以 strm 直链、不响应远程命令的第三方播放器也无法再开播
- 观看统计：每个账号近 7 天（按天）和近 30 天（合计 / 日均 / 观看天数 / 最多的一天）的观看时长表格，含受限媒体库占比；记录保留 31 天，更早的自动删除
- 每个用户可单独设置：是否受限、每日时长（-1 = 用默认值，0 = 不限）
- 管理操作：重置 / 全部重置 / 今日加时（可为负数）/ 临时豁免（N 小时或到下次重置，-1 取消）
- 全局开关，以及“管理员是否受限”开关
- 只会恢复“由插件禁用”的账户，不会动管理员手动禁用的账户

## 编译
    dotnet build -c Release
产物：`bin/Release/netstandard2.0/WatchTimeLimit.dll`

## 安装
把 `WatchTimeLimit.dll` 复制到 Emby 配置目录下的 `plugins/` 中，然后重启 Emby。
设置入口：控制台 → 插件 → Watch Time Limit（侧边栏也会出现一个菜单项）。

重置时间按插件设置里的“时区”计算（默认 Asia/Shanghai，留空则用服务器时区）。

## 数据文件
- 配置：`plugins/configurations/WatchTimeLimit.xml`
- 当日使用量和近 31 天观看记录：`plugins/configurations/WatchTimeLimit.state.json`（只在有变化时写入；仅时长累计时最多每分钟写一次，禁用/恢复/管理操作立即写入）

## REST API（仅管理员）
| 方法 | 路径 | 参数 |
|---|---|---|
| GET  | /emby/WatchTimeLimit/Status | |
| GET  | /emby/WatchTimeLimit/Libraries | |
| GET  | /emby/WatchTimeLimit/Stats | Days（1-31，默认 7） |
| POST | /emby/WatchTimeLimit/Users/{UserId}/Reset | |
| POST | /emby/WatchTimeLimit/ResetAll | |
| POST | /emby/WatchTimeLimit/Users/{UserId}/Bonus | Minutes |
| POST | /emby/WatchTimeLimit/Users/{UserId}/Exempt | Hours（>0 小时数，0 到下次重置，<0 取消） |
| POST | /emby/WatchTimeLimit/Users/{UserId}/Settings | Enabled, DailyLimitMinutes |
| POST | /emby/WatchTimeLimit/Reevaluate | |

## 注意
如果账户因超时被禁用，在 Emby 用户设置里手动“启用”是没用的，下一次检查时会被重新禁用。请在插件页面里用“重置”“加时”或“豁免”。

strm / 网盘直链：客户端直接从网盘取流，Emby 无法切断已经在播放的流；对不响应远程控制的第三方播放器（如 SenPlayer），超时那一刻正在播的内容可能会继续播完，但之后无法再开始新的播放。
卸载插件前请先确认没有用户处于“超时禁用 / 受限库已停用”状态，否则需要手动恢复其账户或媒体库权限。
