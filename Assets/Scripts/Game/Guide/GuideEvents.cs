using GameFramework.Event;

namespace GameFramework.Guide
{
    /// <summary>引导开始（首步已展示）。</summary>
    public struct GuideStartedEvent : IGameEvent
    {
        public string Group;
        public int StepCount;
    }

    /// <summary>切换到了新的一步。</summary>
    public struct GuideStepChangedEvent : IGameEvent
    {
        public int StepId;
        public int Order;
        /// <summary>这一步的目标面板（空 = 全屏对话步）。</summary>
        public string TargetPanel;
        /// <summary>这一步的目标节点（空 = 全屏对话步）。</summary>
        public string TargetNode;
    }

    /// <summary>引导结束（走完 / 跳过都算）。</summary>
    public struct GuideFinishedEvent : IGameEvent
    {
        /// <summary>true = 玩家点了「跳过引导」。</summary>
        public bool Skipped;
        /// <summary>结束时的进度位掩码（已同步给服务端的部分）。</summary>
        public long Mask;
    }
}
