using System;
using System.Collections.Generic;
using GameFramework.Business;
using GameFramework.Config;
using GameFramework.Core;
using GameFramework.Event;
using GameFramework.Localization;
using GameFramework.Log;
using GameFramework.Net.Client.Unity;
using GameFramework.Net.Protocol;
using GameFramework.Platform;
using GameFramework.Storage;
using GameFramework.Timer;
using GameFramework.UI;
using UnityEngine;
using UnityEngine.UI;

namespace GameFramework.Guide
{
    /// <summary>
    /// 新手引导（一次性）：配表驱动步骤 → Top 层遮罩高亮 → 点目标推进 → 进度存服务端玩家属性。
    ///
    /// 分层：
    ///   · 逻辑（本类，热更程序集）：哪一步、什么时候开始、怎么推进、进度怎么同步；
    ///   · 表现（<see cref="GuideOverlay"/>，AOT 的 UIFramework）：遮罩 / 挖洞 / 气泡 / 跳过按钮；
    ///   · 内容（Guide.xlsx + LocZh/LocEn）：步骤、目标节点、文案、遮罩类型、推进方式；
    ///   · 权威（服务端 PlayerData.Attributes[guide_progress]）：位掩码，登录 / 重连时随 player.info.push 下发。
    ///
    /// 断线续做的机制：断开（PlayerOfflineEvent）→ 收遮罩、暂停；重连成功 → 服务端推全量属性 →
    /// 本地进度与权威值按位或 → 从「第一个没做完的步骤」继续。所以不需要额外存「当前是第几步」。
    /// </summary>
    public sealed class GuideManager : IGameModule, ITickable, IDisposable
    {
        /// <summary>本地镜像的 KV 键前缀（按账号区分，避免换账号继承上一个账号的引导进度）。</summary>
        private const string LocalProgressPrefix = "guide_progress:";

        /// <summary>进到哪个面板才算「可以开始引导」（避免在加载/转场时弹全屏遮罩）。</summary>
        private const string LobbyPanelName = "MainPanel";

        private const float TargetWaitSeconds = 4f;

        private readonly IEventBus events;
        private readonly UIManager ui;
        private readonly TimerManager timer;
        private readonly ConfigManager config;
        private readonly LocalizationManager localization;
        private readonly GameClientBehaviour client;
        private readonly BusinessManager business;
        private readonly PlatformManager platform;
        private readonly LocalStorageManager storage;

        private readonly List<IDisposable> subscriptions = new List<IDisposable>();

        private GuidePhase phase = GuidePhase.Idle;
        private bool initialized;
        private bool disposed;

        private List<GuideConfig> steps;
        private GuideConfig current;
        private long completedMask;
        private long pendingMask;
        private string accountKey;

        private GuideOverlay overlay;
        private RectTransform targetRect;
        private Button hookedButton;
        private IDisposable panelSubscription;
        private TimerHandle waitHandle;
        private TimerHandle timeoutHandle;
        private bool lobbyReady;

        public GuideManager(IEventBus events, UIManager ui, TimerManager timer, ConfigManager config,
            LocalizationManager localization, GameClientBehaviour client, BusinessManager business,
            PlatformManager platform, LocalStorageManager storage)
        {
            this.events = events;
            this.ui = ui;
            this.timer = timer;
            this.config = config;
            this.localization = localization;
            this.client = client;
            this.business = business;
            this.platform = platform;
            this.storage = storage;
        }

        /// <summary>引导组（Guide.xlsx 的 Group 列）。换项目时改这里即可。</summary>
        public string GroupId { get; set; } = "newbie_lobby";

        /// <summary>是否走完了（完成或跳过都算走完）。</summary>
        public bool IsFinished { get { return phase == GuidePhase.Finished; } }

        /// <summary>是否正在引导中（返回键拦截、外部系统判断用）。</summary>
        public bool IsRunning { get { return phase == GuidePhase.Running; } }

        /// <summary>本地看到的进度位掩码（含尚未同步到服务端的部分）。</summary>
        public long CompletedMask { get { return completedMask; } }

        /// <summary>当前步骤 Id（0 = 没在引导中）。调试 / 埋点用。</summary>
        public int CurrentStepId { get { return current != null ? current.Id : 0; } }

        public bool IsInitialized { get { return initialized; } }

        private static ModuleLog Log
        {
            get { return GameLog.Get(LogTag.Game); }
        }

        public void Init()
        {
            if (initialized) return;
            initialized = true;

            if (events != null)
            {
                subscriptions.Add(events.Subscribe<PlayerInfoChangedEvent>(OnPlayerInfoChanged));
                subscriptions.Add(events.Subscribe<PlayerOfflineEvent>(OnPlayerOffline));
            }

            if (business != null && business.Hub != null)
                subscriptions.Add(business.Hub.Register<GuideProgressPush>(ProtocolId.GuideProgressPush, OnProgressPush, "Guide"));

            if (ui != null)
            {
                ui.PanelOpened += OnPanelOpened;
                panelSubscription = null;
            }

            if (platform != null)
                platform.AddBackHandler(OnBackPressed);

            LoadLocalMirror();
            Log.Info("新手引导已就绪：组=" + GroupId);
        }

        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            // 断线期间攒下的进度，重新登录后补发
            if (pendingMask != 0) SendProgress();
        }

        public void Shutdown()
        {
            if (disposed) return;
            disposed = true;
            initialized = false;

            for (int i = 0; i < subscriptions.Count; i++)
                subscriptions[i]?.Dispose();
            subscriptions.Clear();

            if (panelSubscription != null)
            {
                panelSubscription.Dispose();
                panelSubscription = null;
            }

            if (ui != null) ui.PanelOpened -= OnPanelOpened;
            if (platform != null) platform.RemoveBackHandler(OnBackPressed);
            if (timer != null) timer.CancelOwner(this);

            UnhookTarget();
            if (overlay != null) overlay.Hide();

            phase = GuidePhase.Idle;
        }

        public void Dispose() { Shutdown(); }

        // ---------------- 事件入口 ----------------

        private void OnPanelOpened(string panelName)
        {
            if (panelName == LobbyPanelName)
                lobbyReady = true;

            if (phase == GuidePhase.Running || phase == GuidePhase.Paused || phase == GuidePhase.Finished)
                return;

            TryStart();
        }

        private void OnPlayerInfoChanged(PlayerInfoChangedEvent evt)
        {
            // 换账号了：本地镜像按账号隔离，先把上一个账号的引导状态整个复位（含已完成标记），
            // 否则「A 账号走完 → 切 B 账号」时 B 永远不会再弹引导。
            string account = business != null && business.Player != null ? business.Player.PlayerName : null;
            if (!string.IsNullOrEmpty(account) && account != accountKey)
            {
                accountKey = account;
                completedMask = 0;
                pendingMask = 0;
                current = null;
                phase = GuidePhase.Idle;

                if (timer != null) timer.CancelOwner(this);
                UnhookTarget();
                if (overlay != null) overlay.Hide();

                LoadLocalMirror();
            }

            if (business != null && business.Player != null
                && business.Player.Attributes.TryGetValue(GuideAttributes.Progress, out long mask))
            {
                ApplyServerMask(mask);
            }

            if (phase == GuidePhase.Paused)
            {
                // 断线重连回来：服务端权威值已到手 → 从第一个没做完的步骤继续
                phase = GuidePhase.Idle;
                TryStart();
                return;
            }

            if (phase == GuidePhase.Idle)
                TryStart();
        }

        private void OnProgressPush(GuideProgressPush push)
        {
            if (push == null) return;
            ApplyServerMask(push.Mask);
        }

        private void OnPlayerOffline(PlayerOfflineEvent evt)
        {
            if (phase != GuidePhase.Running) return;

            // 断线：立刻收遮罩并暂停；进度本身在服务端，重连后会接着做
            phase = GuidePhase.Paused;
            if (timer != null) timer.CancelOwner(this);
            UnhookTarget();
            if (overlay != null) overlay.Hide();
            Log.Info("断线，引导暂停；重连后从第一个未完成步骤继续。");
        }

        private bool OnBackPressed()
        {
            // 引导中吃掉返回键，避免误关弹窗 / 误退后台
            return IsRunning;
        }

        // ---------------- 步骤机 ----------------

        private void TryStart()
        {
            if (phase != GuidePhase.Idle) return;
            if (!lobbyReady) return;

            List<GuideConfig> resolved = ResolveSteps();
            if (resolved == null || resolved.Count == 0)
            {
                Log.Warn("配置表里没有引导组 '" + GroupId + "' 的步骤（Guide.xlsx），本次不引导。");
                phase = GuidePhase.Finished;
                return;
            }

            steps = resolved;

            if (IsAllCompleted(completedMask))
            {
                phase = GuidePhase.Finished;
                MarkDoneCache();
                return;
            }

            GuideConfig step = FirstIncomplete();
            if (step == null)
            {
                phase = GuidePhase.Finished;
                MarkDoneCache();
                return;
            }

            StartStep(step);
        }

        private void StartStep(GuideConfig step)
        {
            current = step;
            phase = GuidePhase.Running;

            if (timer != null) timer.CancelOwner(this);
            UnhookTarget();
            EnsurePanelOpened(step.TargetPanel);

            Publish(new GuideStepChangedEvent
            {
                StepId = step.Id,
                Order = step.Order,
                TargetPanel = step.TargetPanel,
                TargetNode = step.TargetNode
            });

            if (string.IsNullOrEmpty(step.TargetPanel) || string.IsNullOrEmpty(step.TargetNode))
            {
                // 全屏对话步（没有具体目标）
                ShowStep(step, null);
                return;
            }

            RectTransform found = TryFindNode(step.TargetPanel, step.TargetNode);
            if (found != null)
            {
                ShowStep(step, found);
                return;
            }

            // 目标还没出现（面板异步加载 / 动画中 / 节点未注册）：等它出现，最多 4 秒
            timeoutHandle = timer != null
                ? timer.Delay(this, TargetWaitSeconds, OnTargetTimeout)
                : TimerHandle.Invalid;

            waitHandle = timer != null
                ? timer.WaitUntil(this,
                    () =>
                    {
                        RectTransform node = TryFindNode(step.TargetPanel, step.TargetNode);
                        if (node != null) targetRect = node;
                        return node != null;
                    },
                    () =>
                    {
                        if (timeoutHandle.IsValid) timeoutHandle.Cancel();
                        if (targetRect != null) ShowStep(step, targetRect);
                    })
                : TimerHandle.Invalid;
        }

        private void OnTargetTimeout()
        {
            if (phase != GuidePhase.Running || current == null) return;

            Log.WarnFormat("引导步骤 {0} 找不到目标节点：{1}/{2}（{3} 秒超时），跳过这一步。",
                current.Id, current.TargetPanel, current.TargetNode, TargetWaitSeconds);

            Advance();
        }

        private void ShowStep(GuideConfig step, RectTransform target)
        {
            targetRect = target;

            overlay = GuideOverlay.Ensure(localization != null ? localization.Font : null);
            if (overlay == null)
            {
                Log.Error("引导遮罩创建失败（UIRoot 未就绪），跳过这一步。");
                Advance();
                return;
            }

            string tip = ResolveTip(step);
            bool skipAllowed = step.SkipAllowed;
            bool interceptClick;
            GuideHoleShape holeShape = ToHoleShape(step.Shape);
            int radius = step.Radius;

            if (target == null)
            {
                // 全屏步：点任意位置推进；不显示挖洞
                interceptClick = true;
                overlay.Show(null, tip, skipAllowed, interceptClick, holeShape, radius, Advance, OnSkipClicked);
            }
            else if (step.ClickThrough)
            {
                // 如果目标本来就是按钮，直接听它的点击（玩家点的是真按钮）；否则在洞口放透明代理
                Button button = target.GetComponent<Button>();
                if (button != null)
                {
                    hookedButton = button;
                    button.onClick.AddListener(Advance);
                    overlay.Show(target, tip, skipAllowed, false, holeShape, radius, Advance, OnSkipClicked);
                }
                else
                {
                    overlay.Show(target, tip, skipAllowed, true, holeShape, radius, Advance, OnSkipClicked);
                }
            }
            else
            {
                overlay.Show(target, tip, skipAllowed, false, holeShape, radius, null, OnSkipClicked);
            }

            switch (step.AdvanceOn)
            {
                case GuideAdvance.Timer:
                    float seconds = ParseSeconds(step.AdvanceParam, 2f);
                    if (timer != null) timer.Delay(this, seconds, Advance);
                    break;

                case GuideAdvance.ClickTarget:
                case GuideAdvance.ClickAny:
                case GuideAdvance.Manual:
                    // 点击类 / 手动：由点击回调或外部 Advance() 推进
                    break;

                case GuideAdvance.Event:
                    Log.WarnFormat("步骤 {0} 用的推进方式 Event 暂未实现（v1 只支持点击/计时/手动），按手动处理。", step.Id);
                    break;

                default:
                    break;
            }
        }

        /// <summary>推进到下一步（也可由业务手动调用）。</summary>
        public void Advance()
        {
            if (phase != GuidePhase.Running || current == null) return;

            MarkStepDone(current);

            if (IsAllCompleted(completedMask) || NextStepAfter(current) == null)
            {
                Finish(false);
                return;
            }

            GuideConfig next = NextStepAfter(current);
            StartStep(next);
        }

        private void OnSkipClicked()
        {
            if (current == null || !current.SkipAllowed) return;

            // 跳过 = 把所有步骤一次性标记为已完成，之后永远不会再弹
            long all = AllBits();
            completedMask |= all;
            pendingMask |= all;
            SaveLocalMirror();
            SendProgress();

            Finish(true);
        }

        private void Finish(bool skipped)
        {
            phase = GuidePhase.Finished;
            if (timer != null) timer.CancelOwner(this);
            UnhookTarget();
            if (overlay != null) overlay.Hide();
            MarkDoneCache();

            Log.InfoFormat("新手引导结束（{0}），mask={1}", skipped ? "玩家跳过" : "正常走完", completedMask);
            Publish(new GuideFinishedEvent { Skipped = skipped, Mask = completedMask });
        }

        // ---------------- 进度 ----------------

        private void MarkStepDone(GuideConfig step)
        {
            if (step == null || step.Id <= 0 || step.Id >= 63)
            {
                Log.ErrorFormat("步骤 Id={0} 超出位掩码范围（1~62），这一步的进度不会保存。", step != null ? step.Id : -1);
                return;
            }

            long bit = 1L << step.Id;
            completedMask |= bit;
            pendingMask |= bit;
            SaveLocalMirror();
            SendProgress();
        }

        private void ApplyServerMask(long mask)
        {
            if ((mask & ~completedMask) != 0)
            {
                completedMask |= mask;
                SaveLocalMirror();
            }

            if (IsAllCompleted(completedMask) && phase == GuidePhase.Running)
                Finish(false);
        }

        private void SendProgress()
        {
            if (pendingMask == 0) return;
            if (client == null || !client.IsLoggedIn) return;   // 断线：等重连登录后 Tick 里补发

            long mask = pendingMask;
            pendingMask = 0;

            try
            {
                client.Send(ProtocolId.GuideProgressSet, new GuideProgressSetRequest { Mask = mask });
            }
            catch (Exception ex)
            {
                // 发失败就把位还回去，下次 Tick 再补
                pendingMask |= mask;
                Log.WarnFormat("引导进度上报失败：{0}（{1} 秒后重试）", ex.Message, 1f);
            }
        }

        private void LoadLocalMirror()
        {
            if (storage == null || !storage.IsInitialized) return;
            if (string.IsNullOrEmpty(accountKey)) return;

            string raw = storage.GetPref(LocalProgressPrefix + accountKey);
            if (!string.IsNullOrEmpty(raw) && long.TryParse(raw, out long mask))
                completedMask |= mask;
        }

        private void SaveLocalMirror()
        {
            if (storage == null || !storage.IsInitialized) return;
            if (string.IsNullOrEmpty(accountKey)) return;
            storage.SetPref(LocalProgressPrefix + accountKey, completedMask.ToString());
        }

        private void MarkDoneCache()
        {
            if (storage == null || !storage.IsInitialized) return;

            storage.SetPref(LocalProgressPrefix + (accountKey ?? "default"), completedMask.ToString());

            if (storage.Cache != null && !storage.Cache.GuidanceDone)
            {
                storage.Cache.GuidanceDone = true;
                storage.Save(storage.CacheFile);
            }
        }

        // ---------------- 工具 ----------------

        private List<GuideConfig> ResolveSteps()
        {
            if (config == null || config.Database == null) return null;
            TbGuide table = config.Database.Guide;
            if (table == null) return null;

            List<GuideConfig> list = new List<GuideConfig>();
            IReadOnlyList<GuideConfig> rows = table.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                GuideConfig row = rows[i];
                if (row == null) continue;
                if (!string.IsNullOrEmpty(row.Group) && row.Group != GroupId) continue;
                list.Add(row);
            }

            list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Id.CompareTo(b.Id));
            return list;
        }

        private GuideConfig FirstIncomplete()
        {
            if (steps == null) return null;
            for (int i = 0; i < steps.Count; i++)
                if (!IsStepDone(steps[i].Id)) return steps[i];
            return null;
        }

        private GuideConfig NextStepAfter(GuideConfig step)
        {
            if (steps == null || step == null) return null;

            // Next 列非 0 时按显式跳转；否则找 Order 更大的第一个未完成步骤
            if (step.Next > 0)
            {
                GuideConfig explicitNext = steps.Find(x => x.Id == step.Next);
                if (explicitNext != null && !IsStepDone(explicitNext.Id)) return explicitNext;
            }

            for (int i = 0; i < steps.Count; i++)
            {
                GuideConfig candidate = steps[i];
                if (candidate.Order <= step.Order) continue;
                if (!IsStepDone(candidate.Id)) return candidate;
            }

            return null;
        }

        private bool IsStepDone(int id)
        {
            if (id <= 0 || id >= 63) return true;   // 非法 Id 当作已完成，避免死循环；
            return (completedMask & (1L << id)) != 0;
        }

        private bool IsAllCompleted(long mask)
        {
            if (steps == null) return true;
            for (int i = 0; i < steps.Count; i++)
            {
                int id = steps[i].Id;
                if (id <= 0 || id >= 63) continue;
                if ((mask & (1L << id)) == 0) return false;
            }
            return true;
        }

        private long AllBits()
        {
            long bits = 0L;
            if (steps == null) return bits;
            for (int i = 0; i < steps.Count; i++)
            {
                int id = steps[i].Id;
                if (id > 0 && id < 63) bits |= 1L << id;
            }
            return bits;
        }

        private void EnsurePanelOpened(string panelName)
        {
            if (string.IsNullOrEmpty(panelName) || ui == null) return;
            if (ui.IsOpen(panelName)) return;
            ui.Open(panelName);
        }

        private RectTransform TryFindNode(string panelName, string nodeKey)
        {
            if (ui == null || string.IsNullOrEmpty(panelName) || string.IsNullOrEmpty(nodeKey)) return null;

            Panel panel = ui.Get(panelName);
            if (panel == null) return null;

            GameObject node = panel.GetNode(nodeKey);
            if (node == null) return null;

            return node.transform as RectTransform;
        }

        private string ResolveTip(GuideConfig step)
        {
            if (string.IsNullOrEmpty(step.TipKey)) return string.Empty;

            string text = localization != null ? localization.GetText(step.TipKey) : null;
            if (string.IsNullOrEmpty(text) || text == step.TipKey)
                return step.TipKey + "（缺 Loc 文案）";

            return text;
        }

        private static float ParseSeconds(string raw, float fallback)
        {
            if (!string.IsNullOrEmpty(raw) && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value) && value > 0f)
                return value;
            return fallback;
        }

        /// <summary>配表的洞形 → 表现层的洞形（AOT 的 UIFramework 不认识 Config 枚举，所以在这里翻译）。</summary>
        private static GuideHoleShape ToHoleShape(GuideShape shape)
        {
            switch (shape)
            {
                case GuideShape.Circle:
                    return GuideHoleShape.Circle;
                case GuideShape.RoundRect:
                    return GuideHoleShape.RoundRect;
                default:
                    return GuideHoleShape.Rect;
            }
        }

        private void UnhookTarget()
        {
            if (hookedButton != null)
            {
                hookedButton.onClick.RemoveListener(Advance);
                hookedButton = null;
            }

            targetRect = null;
            if (waitHandle.IsValid) waitHandle.Cancel();
            if (timeoutHandle.IsValid) timeoutHandle.Cancel();
        }

        private void Publish<TEvent>(TEvent evt) where TEvent : IGameEvent
        {
            if (events != null) events.Post(evt);
        }

        private enum GuidePhase
        {
            Idle,
            Running,
            Paused,
            Finished
        }
    }
}
