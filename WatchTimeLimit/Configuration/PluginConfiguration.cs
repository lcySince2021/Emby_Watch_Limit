using MediaBrowser.Model.Plugins;

namespace WatchTimeLimit.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>全局开关：关闭后不计时、不禁用（已被插件禁用的账户会被恢复）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>默认每日观看时长（分钟），0 表示不限制。</summary>
        public int DefaultDailyLimitMinutes { get; set; } = 120;

        /// <summary>时区（IANA 名称，如 Asia/Shanghai），留空使用服务器时区。</summary>
        public string TimeZoneId { get; set; } = "Asia/Shanghai";

        /// <summary>每日重置时间（所选时区的小时，0-23）。</summary>
        public int ResetHour { get; set; } = 0;

        /// <summary>剩余多少分钟时向客户端发送提醒，0 表示不提醒。</summary>
        public int WarnMinutesBefore { get; set; } = 5;

        /// <summary>超时时若正在播放，允许看完当前内容的最长分钟数，0 表示立即停止。</summary>
        public int GraceMinutes { get; set; } = 10;

        /// <summary>开始播放时提示今日剩余时长。</summary>
        public bool NotifyRemainingOnPlaybackStart { get; set; } = true;

        /// <summary>受限媒体库（集合文件夹 Id），为空表示所有媒体库都计时。</summary>
        public string[] LimitedLibraryIds { get; set; } = new string[0];

        /// <summary>超时后只停用受限媒体库，不禁用账户（需选择了受限媒体库）。</summary>
        public bool BlockLibrariesOnly { get; set; } = false;

        /// <summary>管理员账户是否也受限制。</summary>
        public bool ApplyToAdministrators { get; set; } = false;

        /// <summary>暂停状态是否计时。</summary>
        public bool CountPausedTime { get; set; } = false;

        /// <summary>超时时是否强制登出（吊销该用户所有登录令牌）。</summary>
        public bool RevokeTokensOnExceed { get; set; } = true;

        public UserLimitSetting[] UserSettings { get; set; } = new UserLimitSetting[0];
    }

    public class UserLimitSetting
    {
        public string UserId { get; set; }

        /// <summary>该用户是否启用限制。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>该用户每日时长（分钟），-1 表示使用默认值，0 表示不限制。</summary>
        public int DailyLimitMinutes { get; set; } = -1;
    }
}
