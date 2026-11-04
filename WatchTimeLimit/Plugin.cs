using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using WatchTimeLimit.Configuration;

namespace WatchTimeLimit
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public const string PluginName = "Watch Time Limit";

        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => PluginName;

        public override string Description => "限制用户每日观看时长，超时自动禁用账户，每天定时重置。";

        public override Guid Id => new Guid("6b0f4f5e-2b7c-4d8e-9a51-3c1d7e2f9a40");

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "WatchTimeLimit",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    EnableInMainMenu = true,
                    MenuIcon = "timer"
                },
                new PluginPageInfo
                {
                    Name = "WatchTimeLimitjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }
    }
}
