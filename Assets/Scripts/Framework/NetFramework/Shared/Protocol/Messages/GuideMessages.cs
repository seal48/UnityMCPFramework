using System;

namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 新手引导的进度存在服务端「玩家属性」里（PlayerData.Attributes 的 long 字典），
    /// 键名两端共用，客户端读 player.info.push 里的属性，服务端负责落库。
    ///
    /// 为什么用属性而不是新加一张表：
    ///   1) 登录 / 重连时 player.info.push 本来就会带全量属性，进度自动同步，断线续做天然成立；
    ///   2) 服务端不用改存储结构（SQLite 的 attributes 是 JSON 列，加键不需要迁移）。
    /// </summary>
    public static class GuideAttributes
    {
        /// <summary>
        /// 已完成步骤的位掩码：bit N = 步骤 Id 为 N 的那一步已完成。
        /// 所以引导步骤的 Id 只能用 1~62（long 的最高位留给符号位）。
        /// </summary>
        public const string Progress = "guide_progress";
    }

    /// <summary>客户端上报引导进度（C → S）。服务端只做「按位或」，进度只能前进不能回退。</summary>
    [Serializable]
    public class GuideProgressSetRequest
    {
        public long Mask;
    }

    /// <summary>服务端回送的权威进度（S → C），客户端拿它覆盖本地镜像。</summary>
    [Serializable]
    public class GuideProgressPush
    {
        public long Mask;
        public long ServerTimeMs;
    }
}
