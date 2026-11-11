define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading) {
    'use strict';

    var pluginId = '6b0f4f5e-2b7c-4d8e-9a51-3c1d7e2f9a40';

    function fmt(seconds) {
        seconds = Math.max(0, Math.round(seconds));
        var h = Math.floor(seconds / 3600);
        var m = Math.floor((seconds % 3600) / 60);
        return h ? (h + '小时' + m + '分') : (m + '分');
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    var weekdays = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];

    function cell(total, limited, hasLimited) {
        if (!total) {
            return '<span style="opacity:.4;">—</span>';
        }
        var html = fmt(total);
        if (hasLimited && limited && Math.round(limited) !== Math.round(total)) {
            html += '<div style="font-size:85%;opacity:.65;">受限 ' + fmt(limited) + '</div>';
        }
        return html;
    }

    function sum(list) {
        return list.reduce(function (a, b) { return a + b; }, 0);
    }

    function post(path, params) {
        return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl(path, params || {}) });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;

        var hourSelect = view.querySelector('#wtlResetHour');
        var html = '';
        for (var i = 0; i < 24; i++) {
            html += '<option value="' + i + '">' + (i < 10 ? '0' : '') + i + ':00</option>';
        }
        hourSelect.innerHTML = html;

        view.querySelector('.wtlConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.Enabled = view.querySelector('#wtlEnabled').checked;
                config.DefaultDailyLimitMinutes = parseInt(view.querySelector('#wtlDefaultLimit').value, 10) || 0;
                config.ResetHour = parseInt(hourSelect.value, 10) || 0;
                config.TimeZoneId = view.querySelector('#wtlTimeZone').value.trim();
                config.WarnMinutesBefore = parseInt(view.querySelector('#wtlWarn').value, 10) || 0;
                config.GraceMinutes = parseInt(view.querySelector('#wtlGrace').value, 10) || 0;
                config.NotifyRemainingOnPlaybackStart = view.querySelector('#wtlNotifyStart').checked;
                config.LimitedLibraryIds = Array.prototype.map.call(
                    view.querySelectorAll('.wtlLibrary:checked'), function (c) { return c.getAttribute('data-id'); });
                config.BlockLibrariesOnly = view.querySelector('#wtlBlockOnly').checked;
                config.ApplyToAdministrators = view.querySelector('#wtlApplyAdmins').checked;
                config.CountPausedTime = view.querySelector('#wtlCountPaused').checked;
                config.RevokeTokensOnExceed = view.querySelector('#wtlRevoke').checked;
                return ApiClient.updatePluginConfiguration(pluginId, config);
            }).then(function () {
                return post('WatchTimeLimit/Reevaluate');
            }).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
                self.loadStatus();
            }, function () {
                loading.hide();
            });
            return false;
        });

        self.statsDays = 7;
        Array.prototype.forEach.call(view.querySelectorAll('.wtlStatsTab'), function (btn) {
            btn.addEventListener('click', function () {
                self.statsDays = parseInt(btn.getAttribute('data-days'), 10);
                self.loadStats();
            });
        });

        view.querySelector('.wtlRefresh').addEventListener('click', function () {
            self.loadStatus();
        });

        view.querySelector('.wtlResetAll').addEventListener('click', function () {
            if (confirm('确定重置所有用户今日的观看时长？')) {
                self.run(post('WatchTimeLimit/ResetAll'));
            }
        });

        view.querySelector('.wtlRows').addEventListener('click', function (e) {
            var btn = e.target.closest('button[data-action]');
            if (!btn) {
                return;
            }
            var row = btn.closest('tr');
            var userId = row.getAttribute('data-id');
            var base = 'WatchTimeLimit/Users/' + userId + '/';
            var action = btn.getAttribute('data-action');

            if (action === 'reset') {
                self.run(post(base + 'Reset'));
            } else if (action === 'bonus') {
                var minutes = prompt('今日临时增加多少分钟？（负数为扣减）', '30');
                if (minutes !== null && !isNaN(parseInt(minutes, 10))) {
                    self.run(post(base + 'Bonus', { Minutes: parseInt(minutes, 10) }));
                }
            } else if (action === 'exempt') {
                var hours = prompt('临时豁免多少小时？（0 = 到下次重置，-1 = 取消豁免）', '0');
                if (hours !== null && !isNaN(parseFloat(hours))) {
                    self.run(post(base + 'Exempt', { Hours: parseFloat(hours) }));
                }
            } else if (action === 'save') {
                self.run(post(base + 'Settings', {
                    Enabled: row.querySelector('.wtlUserEnabled').checked,
                    DailyLimitMinutes: parseInt(row.querySelector('.wtlUserLimit').value, 10)
                }));
            }
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.run = function (promise) {
        var self = this;
        loading.show();
        promise.then(function () {
            self.loadStatus();
        }, function () {
            loading.hide();
            alert('操作失败');
        });
    };

    View.prototype.loadConfig = function () {
        var view = this.view;
        return Promise.all([
            ApiClient.getPluginConfiguration(pluginId),
            ApiClient.getJSON(ApiClient.getUrl('WatchTimeLimit/Libraries'))
        ]).then(function (results) {
            var config = results[0];
            var selected = (config.LimitedLibraryIds || []).map(function (i) { return String(i).toLowerCase(); });
            view.querySelector('.wtlLibraries').innerHTML = results[1].map(function (lib) {
                var checked = selected.indexOf(String(lib.Id).toLowerCase()) !== -1 ? ' checked' : '';
                return '<label style="display:inline-flex;align-items:center;margin:0 1.5em .5em 0;cursor:pointer;">' +
                    '<input type="checkbox" class="wtlLibrary" data-id="' + esc(lib.Id) + '"' + checked + ' style="margin-right:.4em;" />' +
                    esc(lib.Name) + '</label>';
            }).join('') || '<div class="fieldDescription">没有找到媒体库</div>';
            view.querySelector('#wtlBlockOnly').checked = config.BlockLibrariesOnly;
            view.querySelector('#wtlEnabled').checked = config.Enabled;
            view.querySelector('#wtlDefaultLimit').value = config.DefaultDailyLimitMinutes;
            view.querySelector('#wtlResetHour').value = config.ResetHour;
            view.querySelector('#wtlTimeZone').value = config.TimeZoneId || '';
            view.querySelector('#wtlWarn').value = config.WarnMinutesBefore;
            view.querySelector('#wtlGrace').value = config.GraceMinutes;
            view.querySelector('#wtlNotifyStart').checked = config.NotifyRemainingOnPlaybackStart;
            view.querySelector('#wtlApplyAdmins').checked = config.ApplyToAdministrators;
            view.querySelector('#wtlCountPaused').checked = config.CountPausedTime;
            view.querySelector('#wtlRevoke').checked = config.RevokeTokensOnExceed;
        });
    };

    View.prototype.loadStatus = function () {
        var view = this.view;
        loading.show();
        return ApiClient.getJSON(ApiClient.getUrl('WatchTimeLimit/Status')).then(function (users) {
            var html = users.map(function (u) {
                var status = [];
                if (u.IsPlaying) { status.push('<span style="color:#52b54b;">播放中</span>'); }
                if (u.GraceUntil) { status.push('<span style="color:#fb8c00;">超时宽限至 ' + esc(u.GraceUntil.slice(11)) + '</span>'); }
                if (u.IsBlocked) { status.push('<span style="color:#e53935;">超时，受限库已停用</span>'); }
                if (u.DisabledByPlugin) { status.push('<span style="color:#e53935;">超时禁用</span>'); }
                else if (u.IsDisabled) { status.push('<span style="color:#999;">已禁用</span>'); }
                if (u.ExemptUntil) { status.push('<span style="color:#f9a825;">豁免至 ' + esc(u.ExemptUntil) + '</span>'); }
                if (u.IsAdministrator) { status.push('管理员'); }

                var limitText = u.EffectiveLimitMinutes > 0 ? (u.EffectiveLimitMinutes + '分') : '不限';
                if (u.BonusMinutes) { limitText += (u.BonusMinutes > 0 ? ' +' : ' ') + u.BonusMinutes + '分'; }

                return '<tr data-id="' + esc(u.UserId) + '" style="border-top:1px solid rgba(128,128,128,.25);">' +
                    '<td style="padding:.5em;">' + esc(u.Name) + '</td>' +
                    '<td style="padding:.5em;">' + (status.join(' / ') || '正常') + '</td>' +
                    '<td style="padding:.5em;">' + fmt(u.UsedSeconds) + ' / ' + limitText + '</td>' +
                    '<td style="padding:.5em;">' + (u.IsLimited ? fmt(u.RemainingSeconds) : '—') + '</td>' +
                    '<td style="padding:.5em;"><input type="checkbox" class="wtlUserEnabled"' + (u.LimitEnabled ? ' checked' : '') + ' /></td>' +
                    '<td style="padding:.5em;"><input type="number" class="wtlUserLimit" style="width:5em;" min="-1" value="' + u.DailyLimitMinutes + '" /> ' +
                    '<button is="emby-button" type="button" class="raised" data-action="save"><span>保存</span></button></td>' +
                    '<td style="padding:.5em;">' +
                    '<button is="emby-button" type="button" class="raised" data-action="reset"><span>重置</span></button> ' +
                    '<button is="emby-button" type="button" class="raised" data-action="bonus"><span>加时</span></button> ' +
                    '<button is="emby-button" type="button" class="raised" data-action="exempt"><span>豁免</span></button>' +
                    '</td></tr>';
            }).join('');
            view.querySelector('.wtlRows').innerHTML = html;
            loading.hide();
        }, function () {
            loading.hide();
        });
    };

    View.prototype.loadStats = function () {
        var view = this.view;
        var days = this.statsDays || 7;
        Array.prototype.forEach.call(view.querySelectorAll('.wtlStatsTab'), function (btn) {
            btn.style.opacity = parseInt(btn.getAttribute('data-days'), 10) === days ? '1' : '.55';
        });

        return ApiClient.getJSON(ApiClient.getUrl('WatchTimeLimit/Stats', { Days: days })).then(function (stats) {
            var th = function (text, align) {
                return '<th style="padding:.5em;text-align:' + (align || 'right') + ';">' + text + '</th>';
            };
            var td = function (html, align) {
                return '<td style="padding:.5em;vertical-align:top;text-align:' + (align || 'right') + ';">' + html + '</td>';
            };
            var lim = stats.HasLimitedLibraries;
            var users = stats.Users.map(function (u) {
                return {
                    name: u.Name,
                    total: u.TotalSeconds,
                    limited: u.LimitedSeconds,
                    sum: sum(u.TotalSeconds),
                    sumLimited: sum(u.LimitedSeconds)
                };
            }).sort(function (a, b) { return b.sum - a.sum; });

            var head, rows;
            if (days <= 7) {
                head = th('用户', 'left') + stats.Days.map(function (d, i) {
                    var date = new Date(d + 'T00:00:00');
                    var label = i === stats.Days.length - 1 ? '今天' : d.slice(5);
                    return th(label + '<div style="font-size:85%;opacity:.65;font-weight:normal;">' + weekdays[date.getDay()] + '</div>');
                }).join('') + th('合计') + th('日均');

                rows = users.map(function (u) {
                    return '<tr style="border-top:1px solid rgba(128,128,128,.25);">' + td(esc(u.name), 'left') +
                        u.total.map(function (t, i) { return td(cell(t, u.limited[i], lim)); }).join('') +
                        td('<b>' + cell(u.sum, u.sumLimited, lim) + '</b>') +
                        td(cell(u.sum / stats.Days.length, 0, false)) + '</tr>';
                });
            } else {
                head = th('用户', 'left') + th('合计') + (lim ? th('其中受限') : '') + th('日均') + th('观看天数') + th('最多的一天');

                rows = users.map(function (u) {
                    var maxIndex = 0;
                    u.total.forEach(function (t, i) { if (t > u.total[maxIndex]) { maxIndex = i; } });
                    var watchedDays = u.total.filter(function (t) { return t >= 60; }).length;
                    var maxText = u.total[maxIndex]
                        ? stats.Days[maxIndex].slice(5) + '　' + fmt(u.total[maxIndex])
                        : '<span style="opacity:.4;">—</span>';
                    return '<tr style="border-top:1px solid rgba(128,128,128,.25);">' + td(esc(u.name), 'left') +
                        td('<b>' + cell(u.sum, 0, false) + '</b>') +
                        (lim ? td(cell(u.sumLimited, 0, false)) : '') +
                        td(cell(u.sum / stats.Days.length, 0, false)) +
                        td(watchedDays + ' / ' + stats.Days.length) +
                        td(maxText) + '</tr>';
                });
            }

            view.querySelector('.wtlStatsHead').innerHTML = '<tr>' + head + '</tr>';
            view.querySelector('.wtlStatsRows').innerHTML = rows.join('');
        });
    };

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var self = this;
        this.loadConfig().then(function () {
            return self.loadStatus();
        }).then(function () {
            return self.loadStats();
        });
        this.refreshTimer = setInterval(function () {
            // 正在编辑表格时不刷新，避免覆盖输入
            if (!self.view.querySelector('.wtlRows').contains(document.activeElement)) {
                self.loadStatus();
            }
            self.loadStats();
        }, 30000);
    };

    View.prototype.onPause = function () {
        BaseView.prototype.onPause.apply(this, arguments);
        clearInterval(this.refreshTimer);
    };

    return View;
});
