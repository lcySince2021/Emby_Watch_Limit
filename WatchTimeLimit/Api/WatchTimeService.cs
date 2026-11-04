using System.Collections.Generic;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace WatchTimeLimit.Api
{
    [Route("/WatchTimeLimit/Status", "GET", Summary = "获取所有用户的观看时长状态")]
    [Authenticated(Roles = "Admin")]
    public class GetWatchTimeStatus : IReturn<List<UserStatus>>
    {
    }

    [Route("/WatchTimeLimit/Libraries", "GET", Summary = "获取媒体库列表")]
    [Authenticated(Roles = "Admin")]
    public class GetWatchTimeLibraries : IReturn<List<LibraryInfo>>
    {
    }

    [Route("/WatchTimeLimit/Users/{UserId}/Reset", "POST", Summary = "重置用户今日时长")]
    [Authenticated(Roles = "Admin")]
    public class ResetWatchTime : IReturnVoid
    {
        public string UserId { get; set; }
    }

    [Route("/WatchTimeLimit/ResetAll", "POST", Summary = "重置所有用户今日时长")]
    [Authenticated(Roles = "Admin")]
    public class ResetAllWatchTime : IReturnVoid
    {
    }

    [Route("/WatchTimeLimit/Users/{UserId}/Bonus", "POST", Summary = "今日临时增加时长（分钟，可为负数）")]
    [Authenticated(Roles = "Admin")]
    public class AddWatchTimeBonus : IReturnVoid
    {
        public string UserId { get; set; }
        public int Minutes { get; set; }
    }

    [Route("/WatchTimeLimit/Users/{UserId}/Exempt", "POST", Summary = "临时豁免：Hours>0 豁免小时数，0 到下次重置，<0 取消")]
    [Authenticated(Roles = "Admin")]
    public class SetWatchTimeExempt : IReturnVoid
    {
        public string UserId { get; set; }
        public double Hours { get; set; }
    }

    [Route("/WatchTimeLimit/Users/{UserId}/Settings", "POST", Summary = "设置用户是否受限及每日时长（-1 使用默认，0 不限）")]
    [Authenticated(Roles = "Admin")]
    public class UpdateWatchTimeSetting : IReturnVoid
    {
        public string UserId { get; set; }
        public bool Enabled { get; set; }
        public int DailyLimitMinutes { get; set; }
    }

    [Route("/WatchTimeLimit/Reevaluate", "POST", Summary = "全局配置变更后重新评估")]
    [Authenticated(Roles = "Admin")]
    public class ReevaluateWatchTime : IReturnVoid
    {
    }

    public class WatchTimeService : IService
    {
        private static WatchTimeManager Manager => WatchTimeManager.Instance;

        public object Get(GetWatchTimeStatus request) => Manager.GetStatus();

        public object Get(GetWatchTimeLibraries request) => Manager.GetLibraries();

        public void Post(ResetWatchTime request) => Manager.ResetUser(request.UserId);

        public void Post(ResetAllWatchTime request) => Manager.ResetAll();

        public void Post(AddWatchTimeBonus request) => Manager.AddBonus(request.UserId, request.Minutes);

        public void Post(SetWatchTimeExempt request) => Manager.SetExempt(request.UserId, request.Hours);

        public void Post(UpdateWatchTimeSetting request) =>
            Manager.UpdateUserSetting(request.UserId, request.Enabled, request.DailyLimitMinutes);

        public void Post(ReevaluateWatchTime request) => Manager.Reevaluate();
    }
}
