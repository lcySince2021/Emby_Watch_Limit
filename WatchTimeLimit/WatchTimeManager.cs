using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;
using WatchTimeLimit.Configuration;

namespace WatchTimeLimit
{
    public class UserState
    {
        public string UserId { get; set; }
        public double UsedSeconds { get; set; }
        public int BonusMinutes { get; set; }
        public DateTime? ExemptUntilUtc { get; set; }
        public bool DisabledByPlugin { get; set; }

        /// <summary>已发送的提醒级别：0 无，1 剩余 N 分钟，2 剩余 1 分钟。</summary>
        public int WarnLevel { get; set; }

        /// <summary>超时后“看完当前内容”的截止时间。</summary>
        public DateTime? GraceUntilUtc { get; set; }

        /// <summary>进入宽限时正在播放的内容，切换到其他内容即结束宽限。</summary>
        public List<string> GraceItemIds { get; set; }

        /// <summary>“只停用受限媒体库”模式下，今日已超时、受限内容被拦截。</summary>
        public bool Blocked { get; set; }

        /// <summary>插件是否已从该用户的媒体库权限中移除了受限媒体库。</summary>
        public bool FolderAccessRemoved { get; set; }
        public bool SavedEnableAllFolders { get; set; }
        public List<string> RemovedFolderIds { get; set; }
    }

    public class LimitState
    {
        public string Day { get; set; }
        public List<UserState> Users { get; set; } = new List<UserState>();

        /// <summary>每日观看记录，保留 HistoryDays 天。</summary>
        public List<DayHistory> History { get; set; } = new List<DayHistory>();
    }

    public class DayHistory
    {
        public string Day { get; set; }
        public List<UserDayUsage> Users { get; set; } = new List<UserDayUsage>();
    }

    public class UserDayUsage
    {
        public string UserId { get; set; }

        /// <summary>实际观看时长（所有媒体库，暂停是否计入跟随设置）。</summary>
        public double TotalSeconds { get; set; }

        /// <summary>其中受限媒体库的时长。</summary>
        public double LimitedSeconds { get; set; }
    }

    public class WatchStats
    {
        /// <summary>从旧到新，最后一天是今天。</summary>
        public List<string> Days { get; set; }
        public bool HasLimitedLibraries { get; set; }
        public List<UserWatchStats> Users { get; set; }
    }

    public class UserWatchStats
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public double[] TotalSeconds { get; set; }
        public double[] LimitedSeconds { get; set; }
    }

    public class UserStatus
    {
        public string UserId { get; set; }
        public string Name { get; set; }
        public bool IsAdministrator { get; set; }
        public bool IsDisabled { get; set; }
        public bool DisabledByPlugin { get; set; }
        public bool LimitEnabled { get; set; }
        public int DailyLimitMinutes { get; set; }
        public int EffectiveLimitMinutes { get; set; }
        public bool IsLimited { get; set; }
        public double UsedSeconds { get; set; }
        public int BonusMinutes { get; set; }
        public double RemainingSeconds { get; set; }
        public string ExemptUntil { get; set; }
        public string GraceUntil { get; set; }
        public bool IsBlocked { get; set; }
        public bool IsPlaying { get; set; }
    }

    public class LibraryInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class WatchTimeManager : IServerEntryPoint
    {
        private const int TickSeconds = 15;

        /// <summary>仅累计时长变化时，最多每隔这么久写一次磁盘。</summary>
        private const int SaveIntervalSeconds = 60;

        /// <summary>观看记录保留天数（含今天）。</summary>
        private const int HistoryDays = 31;

        /// <summary>开始播放时的剩余时长提示，同一用户的最小间隔。</summary>
        private static readonly TimeSpan StartNoticeInterval = TimeSpan.FromMinutes(10);

        public static WatchTimeManager Instance { get; private set; }

        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ISessionManager _sessionManager;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly string _statePath;
        private readonly object _lock = new object();
        private readonly Dictionary<string, DateTime> _lastStartNotice = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, string[]> _itemLibraryCache = new Dictionary<long, string[]>();

        private LimitState _state;
        private Timer _timer;
        private DateTime _lastTickUtc;
        private DateTime _lastSaveUtc;
        private bool _dirty;
        private bool _saveNow;

        public WatchTimeManager(IUserManager userManager, ILibraryManager libraryManager, ISessionManager sessionManager,
            IJsonSerializer json, ILogManager logManager, IApplicationPaths appPaths)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _json = json;
            _logger = logManager.GetLogger("WatchTimeLimit");
            _statePath = Path.Combine(appPaths.PluginConfigurationsPath, "WatchTimeLimit.state.json");
            Instance = this;
        }

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        public void Run()
        {
            LoadState();
            _lastTickUtc = DateTime.UtcNow;
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(TickSeconds), TimeSpan.FromSeconds(TickSeconds));
            _logger.Info("WatchTimeLimit started, state day {0}", _state.Day);
        }

        public void Dispose()
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _timer?.Dispose();
            _timer = null;
            lock (_lock)
            {
                if (_dirty)
                {
                    SaveState();
                }
            }
        }

        #region State

        private void LoadState()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_statePath))
                    {
                        _state = _json.DeserializeFromFile<LimitState>(_statePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Failed to load state", ex);
                }

                if (_state == null)
                {
                    _state = new LimitState { Day = CurrentDay() };
                    MarkDirty(true);
                }
                if (_state.Users == null)
                {
                    _state.Users = new List<UserState>();
                }
                if (_state.History == null)
                {
                    _state.History = new List<DayHistory>();
                }
                PruneHistory();
            }
        }

        private UserDayUsage GetDayUsage(string userId)
        {
            var day = _state.History.FirstOrDefault(d => d.Day == _state.Day);
            if (day == null)
            {
                day = new DayHistory { Day = _state.Day };
                _state.History.Add(day);
            }
            var usage = day.Users.FirstOrDefault(u => string.Equals(u.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (usage == null)
            {
                usage = new UserDayUsage { UserId = userId };
                day.Users.Add(usage);
            }
            return usage;
        }

        /// <summary>删除 HistoryDays 天之前的记录。</summary>
        private void PruneHistory()
        {
            if (!DateTime.TryParseExact(_state.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var today))
            {
                return;
            }
            var oldest = today.AddDays(-(HistoryDays - 1)).ToString("yyyy-MM-dd");
            if (_state.History.RemoveAll(d => d.Day == null || string.CompareOrdinal(d.Day, oldest) < 0) > 0)
            {
                MarkDirty(true);
            }
        }

        /// <param name="important">为 true 时下一次检查立即落盘（禁用/恢复账户、管理员操作等）。</param>
        private void MarkDirty(bool important = false)
        {
            _dirty = true;
            if (important)
            {
                _saveNow = true;
            }
        }

        private void SaveIfNeeded()
        {
            if (!_dirty)
            {
                return;
            }
            if (!_saveNow && (DateTime.UtcNow - _lastSaveUtc).TotalSeconds < SaveIntervalSeconds)
            {
                return;
            }
            SaveState();
        }

        private void SaveState()
        {
            try
            {
                _json.SerializeToFile(_state, _statePath);
                _dirty = false;
                _saveNow = false;
                _lastSaveUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to save state", ex);
            }
        }

        private static TimeZoneInfo GetTimeZone()
        {
            var id = Config.TimeZoneId;
            if (!string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
                }
                catch (Exception)
                {
                }
            }
            return TimeZoneInfo.Local;
        }

        private static DateTime LocalNow()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GetTimeZone());
        }

        private static string ToLocalString(DateTime utc)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utc, GetTimeZone()).ToString("yyyy-MM-dd HH:mm");
        }

        private static string CurrentDay()
        {
            var hour = Math.Max(0, Math.Min(23, Config.ResetHour));
            return LocalNow().AddHours(-hour).ToString("yyyy-MM-dd");
        }

        /// <summary>下一次重置时刻（UTC）。</summary>
        private static DateTime NextResetUtc()
        {
            var hour = Math.Max(0, Math.Min(23, Config.ResetHour));
            var now = LocalNow();
            var next = now.Date.AddHours(hour);
            if (next <= now)
            {
                next = next.AddDays(1);
            }
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), GetTimeZone());
        }

        private UserState GetState(string userId)
        {
            var s = _state.Users.FirstOrDefault(i => string.Equals(i.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (s == null)
            {
                s = new UserState { UserId = userId };
                _state.Users.Add(s);
                MarkDirty();
            }
            return s;
        }

        private static UserLimitSetting GetSetting(string userId)
        {
            return (Config.UserSettings ?? new UserLimitSetting[0])
                .FirstOrDefault(i => string.Equals(i.UserId, userId, StringComparison.OrdinalIgnoreCase));
        }

        private void ClearDaily(UserState s)
        {
            s.UsedSeconds = 0;
            s.BonusMinutes = 0;
            s.WarnLevel = 0;
            s.GraceUntilUtc = null;
            s.GraceItemIds = null;
            // Blocked 留给 Evaluate 去恢复媒体库权限
            MarkDirty(true);
        }

        #endregion

        #region Libraries

        private static HashSet<string> LimitedLibraries()
        {
            return new HashSet<string>(
                (Config.LimitedLibraryIds ?? new string[0]).Where(i => !string.IsNullOrWhiteSpace(i)),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>超时后只拦截受限媒体库，不禁用账户。</summary>
        private static bool BlockMode => Config.BlockLibrariesOnly && LimitedLibraries().Count > 0;

        public List<LibraryInfo> GetLibraries()
        {
            var list = new List<LibraryInfo>();
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                BaseItem item = null;
                if (Guid.TryParse(folder.ItemId, out var guid))
                {
                    item = _libraryManager.GetItemById(guid);
                }
                if (item == null && long.TryParse(folder.ItemId ?? folder.Id, out var internalId))
                {
                    item = _libraryManager.GetItemById(internalId);
                }
                if (item != null)
                {
                    list.Add(new LibraryInfo { Id = item.Id.ToString("N"), Name = folder.Name });
                }
            }
            return list;
        }

        private string[] GetItemLibraryIds(BaseItem item)
        {
            if (_itemLibraryCache.TryGetValue(item.InternalId, out var ids))
            {
                return ids;
            }
            ids = _libraryManager.GetCollectionFolders(item).Select(f => f.Id.ToString("N")).ToArray();
            if (_itemLibraryCache.Count > 2000)
            {
                _itemLibraryCache.Clear();
            }
            _itemLibraryCache[item.InternalId] = ids;
            return ids;
        }

        /// <summary>是否属于受限媒体库；未选择媒体库时全部受限，无法判断时按受限处理。</summary>
        private bool IsLimitedItem(BaseItem item, HashSet<string> libs)
        {
            if (libs.Count == 0 || item == null)
            {
                return true;
            }
            try
            {
                var ids = GetItemLibraryIds(item);
                return ids.Length == 0 || ids.Any(libs.Contains);
            }
            catch (Exception ex)
            {
                _logger.Debug("Resolve library failed: {0}", ex.Message);
                return true;
            }
        }

        private bool IsLimitedSession(SessionInfo session, HashSet<string> libs)
        {
            if (libs.Count == 0)
            {
                return true;
            }
            var item = session.FullNowPlayingItem;
            if (item == null && long.TryParse(session.NowPlayingItem?.Id, out var internalId))
            {
                item = _libraryManager.GetItemById(internalId);
            }
            return IsLimitedItem(item, libs);
        }

        #endregion

        #region Core loop

        private void Tick()
        {
            try
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    var elapsed = Math.Min((now - _lastTickUtc).TotalSeconds, TickSeconds * 3);
                    _lastTickUtc = now;

                    var rolled = CheckRollover();
                    var watching = GetWatchingUserIds(out var limitedWatching);
                    var playing = Config.Enabled ? limitedWatching : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (elapsed > 0)
                    {
                        // 同一用户多个会话同时播放只按真实时间计一次
                        foreach (var userId in watching)
                        {
                            var usage = GetDayUsage(userId);
                            usage.TotalSeconds += elapsed;
                            if (limitedWatching.Contains(userId))
                            {
                                usage.LimitedSeconds += elapsed;
                            }
                            MarkDirty();
                        }
                        foreach (var userId in playing)
                        {
                            GetState(userId).UsedSeconds += elapsed;
                        }
                    }

                    // 没人播放、也没有待恢复/宽限/豁免中的账户时，无需逐个检查
                    var needEvaluate = rolled || playing.Count > 0 || _state.Users.Any(s =>
                        s.DisabledByPlugin || s.Blocked || s.ExemptUntilUtc.HasValue || s.GraceUntilUtc.HasValue);
                    if (needEvaluate)
                    {
                        foreach (var user in GetAllUsers())
                        {
                            Evaluate(user);
                        }
                    }

                    SaveIfNeeded();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Tick failed", ex);
            }
        }

        private bool CheckRollover()
        {
            var day = CurrentDay();
            if (string.Equals(day, _state.Day, StringComparison.Ordinal))
            {
                return false;
            }

            _logger.Info("New day {0}, resetting watch time", day);
            _state.Day = day;
            foreach (var s in _state.Users)
            {
                // DisabledByPlugin 留给 Evaluate 去恢复账户
                ClearDaily(s);
            }
            PruneHistory();
            MarkDirty(true);
            return true;
        }

        /// <summary>正在计时的用户：播放受限媒体库内容，且未暂停（除非开启了暂停计时）。</summary>
        private HashSet<string> GetPlayingUserIds()
        {
            GetWatchingUserIds(out var limited);
            return limited;
        }

        /// <summary>正在观看的用户（未暂停，除非开启了暂停计时）；limited 为其中播放受限媒体库内容的用户。</summary>
        private HashSet<string> GetWatchingUserIds(out HashSet<string> limited)
        {
            var countPaused = Config.CountPausedTime;
            var libs = LimitedLibraries();
            var sessions = _sessionManager.Sessions
                .Where(s => !string.IsNullOrEmpty(s.UserId) && s.NowPlayingItem != null)
                .Where(s => countPaused || s.PlayState == null || !s.PlayState.IsPaused)
                .ToList();
            limited = new HashSet<string>(
                sessions.Where(s => IsLimitedSession(s, libs)).Select(s => s.UserId),
                StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(sessions.Select(s => s.UserId), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>该用户正在播放（含暂停）受限内容的会话。</summary>
        private List<SessionInfo> GetLimitedPlayingSessions(string userId)
        {
            var libs = LimitedLibraries();
            return UserSessions(userId)
                .Where(s => s.NowPlayingItem != null && !string.IsNullOrEmpty(s.NowPlayingItem.Id))
                .Where(s => IsLimitedSession(s, libs))
                .ToList();
        }

        private HashSet<string> GetNowPlayingItemIds(string userId)
        {
            return new HashSet<string>(GetLimitedPlayingSessions(userId).Select(s => s.NowPlayingItem.Id),
                StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<User> GetAllUsers()
        {
            return _userManager.GetUserList(new UserQuery());
        }

        /// <summary>返回 null 表示该用户当前不受限制。</summary>
        private int? GetLimitMinutes(User user, UserState state, out int configuredLimit, out bool limitEnabled)
        {
            var setting = GetSetting(user.Id.ToString("N"));
            limitEnabled = setting == null || setting.Enabled;
            configuredLimit = setting?.DailyLimitMinutes ?? -1;
            var limit = configuredLimit < 0 ? Config.DefaultDailyLimitMinutes : configuredLimit;

            if (!Config.Enabled || !limitEnabled || limit <= 0)
            {
                return null;
            }
            if (user.Policy.IsAdministrator && !Config.ApplyToAdministrators)
            {
                return null;
            }
            if (state.ExemptUntilUtc.HasValue && state.ExemptUntilUtc.Value > DateTime.UtcNow)
            {
                return null;
            }
            return limit;
        }

        private static double Remaining(int limitMinutes, UserState state)
        {
            return (limitMinutes + state.BonusMinutes) * 60.0 - state.UsedSeconds;
        }

        private void Evaluate(User user)
        {
            var userId = user.Id.ToString("N");
            var state = GetState(userId);
            if (state.ExemptUntilUtc.HasValue && state.ExemptUntilUtc.Value <= DateTime.UtcNow)
            {
                state.ExemptUntilUtc = null;
                MarkDirty(true);
            }

            var limit = GetLimitMinutes(user, state, out _, out _);
            if (limit == null)
            {
                ClearGrace(state);
                ClearBlocked(user, state);
                if (state.DisabledByPlugin)
                {
                    SetDisabled(user, state, false);
                }
                return;
            }

            var remaining = Remaining(limit.Value, state);
            if (remaining <= 0)
            {
                if (BlockMode)
                {
                    OnExceededBlock(user, userId, state);
                }
                else
                {
                    OnExceeded(user, userId, state, limit.Value);
                }
                return;
            }

            ClearGrace(state);
            ClearBlocked(user, state);
            if (state.DisabledByPlugin)
            {
                SetDisabled(user, state, false);
            }

            UpdateWarning(userId, state, remaining);
        }

        private void OnExceeded(User user, string userId, UserState state, int limit)
        {
            ClearBlocked(user, state);
            if (state.DisabledByPlugin)
            {
                // 管理员在 Emby 后台手动启用了账户但未重置时长，重新禁用
                if (!user.Policy.IsDisabled)
                {
                    SetDisabled(user, state, true);
                }
                return;
            }
            if (user.Policy.IsDisabled)
            {
                // 管理员手动禁用的账户，不接管
                return;
            }

            if (InGrace(user, userId, state, "之后账户将暂停使用。"))
            {
                return;
            }

            _logger.Info("User {0} exceeded daily watch time ({1} min)", user.Name, limit + state.BonusMinutes);
            ClearGrace(state);
            StopAndNotify(userId, "今日观看时长已用完", "账户已暂停使用，将在下次重置后恢复。");
            SetDisabled(user, state, true);
        }

        /// <summary>“只停用受限媒体库”模式：账户保持可用，拦截受限内容。</summary>
        private void OnExceededBlock(User user, string userId, UserState state)
        {
            if (state.DisabledByPlugin)
            {
                // 从“禁用账户”模式切换过来
                SetDisabled(user, state, false);
            }
            if (user.Policy.IsDisabled)
            {
                return;
            }

            // 一超时就移除受限库权限：当前这集已拿到播放地址可以继续，下一集拿不到 PlaybackInfo。
            // 宽限只推迟对当前这集发送停止命令，且只在刚超时那一次开始。
            var firstTime = !state.Blocked;
            ApplyBlock(user, state);
            var inGrace = firstTime
                ? InGrace(user, userId, state, "下一集起受限内容将无法播放。")
                : IsGraceActive(userId, state);
            if (inGrace)
            {
                return;
            }

            StopSessions(GetLimitedPlayingSessions(userId), "今日观看时长已用完", BlockedMessage);
        }

        private const string BlockedMessage = "受限媒体库今天已不能播放，其他内容不受影响。";

        /// <summary>超时时正在播放则进入宽限，返回 true 表示仍在宽限中、暂不处理。</summary>
        private bool InGrace(User user, string userId, UserState state, string afterText)
        {
            var grace = Config.GraceMinutes;
            if (grace <= 0)
            {
                return false;
            }

            var items = GetNowPlayingItemIds(userId);
            if (!state.GraceUntilUtc.HasValue)
            {
                if (items.Count == 0)
                {
                    return false;
                }
                state.GraceUntilUtc = DateTime.UtcNow.AddMinutes(grace);
                state.GraceItemIds = items.ToList();
                MarkDirty(true);
                _logger.Info("User {0} exceeded daily watch time, grace {1} min for current item", user.Name, grace);
                Notify(userId, "今日观看时长已用完",
                    string.Format("可以看完当前这集（最多 {0} 分钟），{1}", grace, afterText));
                return true;
            }

            // 宽限中，仍在看同一内容
            return IsGraceActive(userId, state);
        }

        private bool IsGraceActive(string userId, UserState state)
        {
            return state.GraceUntilUtc.HasValue && DateTime.UtcNow < state.GraceUntilUtc.Value &&
                   state.GraceItemIds != null && GetNowPlayingItemIds(userId).Overlaps(state.GraceItemIds);
        }

        private void ApplyBlock(User user, UserState state)
        {
            if (!state.Blocked)
            {
                state.Blocked = true;
                MarkDirty(true);
                _logger.Info("User {0} exceeded daily watch time, limited libraries blocked", user.Name);
            }
            if (!state.FolderAccessRemoved)
            {
                RemoveLibraryAccess(user, state);
            }
        }

        private void ClearBlocked(User user, UserState state)
        {
            if (state.FolderAccessRemoved)
            {
                RestoreLibraryAccess(user, state);
            }
            if (state.Blocked)
            {
                state.Blocked = false;
                MarkDirty(true);
            }
        }

        /// <summary>
        /// 临时移除受限媒体库的访问权限。strm/网盘直链的客户端不经过 Emby 取流，
        /// 也未必响应远程停止命令，只有拿不到 PlaybackInfo 才能真正阻止播放。
        /// </summary>
        private void RemoveLibraryAccess(User user, UserState state)
        {
            try
            {
                var libs = LimitedLibraries();
                var policy = user.Policy;
                var current = policy.EnableAllFolders
                    ? GetLibraries().Select(l => l.Id).ToList()
                    : (policy.EnabledFolders ?? new string[0]).ToList();

                state.SavedEnableAllFolders = policy.EnableAllFolders;
                state.RemovedFolderIds = current.Where(libs.Contains).ToList();
                state.FolderAccessRemoved = true;
                MarkDirty(true);

                policy.EnableAllFolders = false;
                policy.EnabledFolders = current.Where(id => !libs.Contains(id)).ToArray();
                _userManager.UpdateUserPolicy(user.InternalId, policy);
                _logger.Info("Removed limited library access for user {0}", user.Name);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to remove library access for user {0}", ex, user.Name);
            }
        }

        private void RestoreLibraryAccess(User user, UserState state)
        {
            try
            {
                var policy = user.Policy;
                if (state.SavedEnableAllFolders)
                {
                    policy.EnableAllFolders = true;
                }
                else
                {
                    // 只加回被插件移除的库，保留期间管理员对其他库权限的修改
                    policy.EnabledFolders = (policy.EnabledFolders ?? new string[0])
                        .Concat(state.RemovedFolderIds ?? new List<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                _userManager.UpdateUserPolicy(user.InternalId, policy);

                state.FolderAccessRemoved = false;
                state.RemovedFolderIds = null;
                MarkDirty(true);
                _logger.Info("Restored library access for user {0}", user.Name);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to restore library access for user {0}", ex, user.Name);
            }
        }

        private void ClearGrace(UserState state)
        {
            if (state.GraceUntilUtc.HasValue || state.GraceItemIds != null)
            {
                state.GraceUntilUtc = null;
                state.GraceItemIds = null;
                MarkDirty(true);
            }
        }

        private void UpdateWarning(string userId, UserState state, double remaining)
        {
            var warn = Config.WarnMinutesBefore;
            var level = 0;
            if (warn > 0)
            {
                if (warn > 1 && remaining <= 60)
                {
                    level = 2;
                }
                else if (remaining <= warn * 60)
                {
                    level = 1;
                }
            }

            if (level > state.WarnLevel)
            {
                var text = string.Format("今日剩余观看时长约 {0} 分钟。", Math.Ceiling(remaining / 60));
                if (Config.GraceMinutes > 0)
                {
                    text += string.Format("到时可看完当前内容（最多 {0} 分钟）。", Config.GraceMinutes);
                }
                Notify(userId, "观看时长提醒", text);
            }
            if (level != state.WarnLevel)
            {
                // 加时后剩余变多时降级，之后还会再次提醒
                state.WarnLevel = level;
                MarkDirty();
            }
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                var session = e.Session;
                if (session == null || string.IsNullOrEmpty(session.UserId))
                {
                    return;
                }

                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    var user = _userManager.GetUserById(session.UserId);
                    if (user == null)
                    {
                        return;
                    }
                    CheckRollover();
                    var state = GetState(user.Id.ToString("N"));
                    var limit = GetLimitMinutes(user, state, out _, out _);
                    if (limit == null)
                    {
                        return;
                    }
                    if (!IsLimitedItem(e.Item, LimitedLibraries()))
                    {
                        // 不受限的媒体库，不提示也不拦截
                        return;
                    }

                    var remaining = Remaining(limit.Value, state);
                    if (remaining <= 0)
                    {
                        // 超时后新开播的受限内容立即停止（宽限只针对超时那一刻正在播放的内容）
                        var itemId = e.MediaInfo?.Id ?? e.Item?.InternalId.ToString();
                        var inGrace = state.GraceUntilUtc.HasValue && now < state.GraceUntilUtc.Value &&
                                      state.GraceItemIds != null && state.GraceItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase);
                        if (!inGrace && !user.Policy.IsDisabled)
                        {
                            if (BlockMode)
                            {
                                ApplyBlock(user, state);
                                StopSessions(new[] { session }, "今日观看时长已用完", BlockedMessage);
                            }
                            else
                            {
                                // 宽限期内换了内容（如自动下一集），立即结束宽限并禁用
                                _logger.Info("User {0} started new content after exceeding daily watch time", user.Name);
                                ClearGrace(state);
                                StopAndNotify(session.UserId, "今日观看时长已用完", "账户已暂停使用，将在下次重置后恢复。");
                                SetDisabled(user, state, true);
                            }
                        }
                        return;
                    }

                    if (!Config.NotifyRemainingOnPlaybackStart ||
                        (_lastStartNotice.TryGetValue(session.UserId, out var last) && now - last < StartNoticeInterval))
                    {
                        return;
                    }

                    _lastStartNotice[session.UserId] = now;
                    SendMessage(session, "观看时长", string.Format("今日剩余观看时长 {0}。", FormatDuration(remaining)), 5000);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("PlaybackStart handler failed", ex);
            }
        }

        private static string FormatDuration(double seconds)
        {
            var minutes = (int)Math.Ceiling(seconds / 60);
            return minutes >= 60
                ? string.Format("{0} 小时 {1} 分钟", minutes / 60, minutes % 60)
                : string.Format("{0} 分钟", minutes);
        }

        private void SetDisabled(User user, UserState state, bool disabled)
        {
            try
            {
                var policy = user.Policy;
                if (policy.IsDisabled != disabled)
                {
                    policy.IsDisabled = disabled;
                    _userManager.UpdateUserPolicy(user.InternalId, policy);
                }
                state.DisabledByPlugin = disabled;
                MarkDirty(true);
                _logger.Info("User {0} {1} by WatchTimeLimit", user.Name, disabled ? "disabled" : "re-enabled");

                if (disabled && Config.RevokeTokensOnExceed)
                {
                    _sessionManager.RevokeUserTokens(user.InternalId, null);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to update policy for user {0}", ex, user.Name);
            }
        }

        private IEnumerable<SessionInfo> UserSessions(string userId)
        {
            return _sessionManager.Sessions
                .Where(s => string.Equals(s.UserId, userId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void SendMessage(SessionInfo session, string header, string text, int timeoutMs)
        {
            try
            {
                _sessionManager.SendMessageCommand(null, session.Id,
                    new MessageCommand { Header = header, Text = text, TimeoutMs = timeoutMs }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Debug("Send message failed: {0}", ex.Message);
            }
        }

        private void Notify(string userId, string header, string text)
        {
            foreach (var session in UserSessions(userId))
            {
                SendMessage(session, header, text, 10000);
            }
        }

        private void StopAndNotify(string userId, string header, string text)
        {
            StopSessions(UserSessions(userId), header, text);
        }

        private void StopSessions(IEnumerable<SessionInfo> sessions, string header, string text)
        {
            foreach (var session in sessions)
            {
                SendMessage(session, header, text, 15000);
                if (session.NowPlayingItem == null)
                {
                    continue;
                }
                try
                {
                    _sessionManager.SendPlaystateCommand(null, session.Id,
                        new PlaystateRequest { Command = PlaystateCommand.Stop }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Stop playback failed: {0}", ex.Message);
                }
            }
        }

        #endregion

        #region Admin operations

        public List<UserStatus> GetStatus()
        {
            lock (_lock)
            {
                CheckRollover();
                var playing = GetPlayingUserIds();
                var list = new List<UserStatus>();
                foreach (var user in GetAllUsers().OrderBy(u => u.Name))
                {
                    var userId = user.Id.ToString("N");
                    var state = GetState(userId);
                    var limit = GetLimitMinutes(user, state, out var configured, out var enabled);
                    var effective = configured < 0 ? Config.DefaultDailyLimitMinutes : configured;
                    var exempt = state.ExemptUntilUtc.HasValue && state.ExemptUntilUtc.Value > DateTime.UtcNow;

                    list.Add(new UserStatus
                    {
                        UserId = userId,
                        Name = user.Name,
                        IsAdministrator = user.Policy.IsAdministrator,
                        IsDisabled = user.Policy.IsDisabled,
                        DisabledByPlugin = state.DisabledByPlugin,
                        LimitEnabled = enabled,
                        DailyLimitMinutes = configured,
                        EffectiveLimitMinutes = effective,
                        IsLimited = limit != null,
                        UsedSeconds = Math.Round(state.UsedSeconds),
                        BonusMinutes = state.BonusMinutes,
                        RemainingSeconds = limit == null ? -1 : Math.Max(0, Math.Round(Remaining(limit.Value, state))),
                        ExemptUntil = exempt ? ToLocalString(state.ExemptUntilUtc.Value) : null,
                        GraceUntil = state.GraceUntilUtc.HasValue && state.GraceUntilUtc.Value > DateTime.UtcNow
                            ? ToLocalString(state.GraceUntilUtc.Value) : null,
                        IsBlocked = state.Blocked,
                        IsPlaying = playing.Contains(userId)
                    });
                }
                SaveIfNeeded();
                return list;
            }
        }

        /// <summary>最近 days 天（含今天）每个用户每天的观看时长。</summary>
        public WatchStats GetStats(int days)
        {
            days = Math.Max(1, Math.Min(HistoryDays, days));
            lock (_lock)
            {
                CheckRollover();
                var today = DateTime.ParseExact(_state.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var dayKeys = Enumerable.Range(0, days)
                    .Select(i => today.AddDays(i - days + 1).ToString("yyyy-MM-dd"))
                    .ToList();
                var history = _state.History.Where(d => d.Day != null).GroupBy(d => d.Day)
                    .ToDictionary(g => g.Key, g => g.SelectMany(d => d.Users).ToList());

                var users = GetAllUsers().OrderBy(u => u.Name).Select(user =>
                {
                    var userId = user.Id.ToString("N");
                    var usages = dayKeys.Select(day => history.TryGetValue(day, out var list)
                        ? list.FirstOrDefault(u => string.Equals(u.UserId, userId, StringComparison.OrdinalIgnoreCase))
                        : null).ToList();
                    return new UserWatchStats
                    {
                        UserId = userId,
                        Name = user.Name,
                        TotalSeconds = usages.Select(u => Math.Round(u?.TotalSeconds ?? 0)).ToArray(),
                        LimitedSeconds = usages.Select(u => Math.Round(u?.LimitedSeconds ?? 0)).ToArray()
                    };
                }).ToList();

                SaveIfNeeded();
                return new WatchStats
                {
                    Days = dayKeys,
                    HasLimitedLibraries = LimitedLibraries().Count > 0,
                    Users = users
                };
            }
        }

        private void Modify(string userId, Action<UserState> action)
        {
            lock (_lock)
            {
                CheckRollover();
                var user = _userManager.GetUserById(userId) ?? throw new ArgumentException("User not found: " + userId);
                var state = GetState(user.Id.ToString("N"));
                action(state);
                MarkDirty(true);
                Evaluate(user);
                SaveIfNeeded();
            }
        }

        public void ResetUser(string userId)
        {
            Modify(userId, ClearDaily);
        }

        public void ResetAll()
        {
            foreach (var user in GetAllUsers().ToList())
            {
                ResetUser(user.Id.ToString("N"));
            }
        }

        /// <summary>今日临时增加（或减少）时长，在下次重置时清零。</summary>
        public void AddBonus(string userId, int minutes)
        {
            Modify(userId, s => s.BonusMinutes = Math.Max(-100000, s.BonusMinutes + minutes));
        }

        /// <summary>临时豁免：hours &gt; 0 豁免指定小时数；0 豁免到下次重置；&lt; 0 取消豁免。</summary>
        public void SetExempt(string userId, double hours)
        {
            Modify(userId, s =>
            {
                if (hours < 0)
                {
                    s.ExemptUntilUtc = null;
                }
                else if (hours == 0)
                {
                    s.ExemptUntilUtc = NextResetUtc();
                }
                else
                {
                    s.ExemptUntilUtc = DateTime.UtcNow.AddHours(hours);
                }
            });
        }

        public void UpdateUserSetting(string userId, bool enabled, int dailyLimitMinutes)
        {
            var user = _userManager.GetUserById(userId) ?? throw new ArgumentException("User not found: " + userId);
            var id = user.Id.ToString("N");
            var config = Config;
            var settings = (config.UserSettings ?? new UserLimitSetting[0])
                .Where(i => !string.Equals(i.UserId, id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            settings.Add(new UserLimitSetting { UserId = id, Enabled = enabled, DailyLimitMinutes = dailyLimitMinutes });
            config.UserSettings = settings.ToArray();
            Plugin.Instance.SaveConfiguration();

            Modify(id, _ => { });
        }

        /// <summary>全局配置变更后立即重新评估所有用户。</summary>
        public void Reevaluate()
        {
            lock (_lock)
            {
                CheckRollover();
                foreach (var user in GetAllUsers())
                {
                    Evaluate(user);
                }
                SaveIfNeeded();
            }
        }

        #endregion
    }
}
