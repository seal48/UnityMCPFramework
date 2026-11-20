using System;
using GameFramework.Net.Protocol;
using GameFramework.Net.Server.Persistence;

namespace GameFramework.Net.Server
{
    /// <summary>
    /// 新手引导进度协议：客户端上报「完成了哪几步」→ 服务端按位或合并进玩家属性 → 回送权威值。
    ///
    /// 设计取舍：
    ///   1) **只前进不回退**：收到的 mask 与库里的 mask 按位或，客户端改包也只能多点亮几步，
    ///      不会把已经完成的引导重置掉（否则改包就能让玩家反复弹引导）；
    ///   2) 进度落在 PlayerData.Attributes 里，登录时 player.info.push 会带下去，客户端断线重连后
    ///      拿到的是服务端权威值，天然支持「断点续做」；
    ///   3) 未登录的连接框架层已经拦掉了（RequireLogin），这里再判一次做双保险。
    /// </summary>
    public static class GuideProtocols
    {
        public static void Register(GameServer server, AccountService accounts)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));

            server.Router.RegisterOrReplace(ProtocolId.GuideProgressSet, (message, peer) =>
            {
                var session = PeerSession.Get(peer);
                if (session == null || !session.LoggedIn)
                {
                    server.Logger.Warn($"[{peer.PeerId}] 未登录的引导进度上报被丢弃");
                    return;
                }

                var request = message.Deserialize<GuideProgressSetRequest>(peer.Serializer);
                if (request == null) return;

                var account = AccountNameRules.Normalize(session.Account);
                if (string.IsNullOrEmpty(account) || accounts == null) return;

                long stored = 0;
                AccountRecord record;
                if (accounts.TryGet(account, out record) && record != null && record.Player != null
                    && record.Player.Attributes != null
                    && record.Player.Attributes.TryGetValue(GuideAttributes.Progress, out long value))
                {
                    stored = value;
                }

                long merged = stored | request.Mask;

                if (!accounts.SetAttribute(account, GuideAttributes.Progress, merged))
                {
                    server.Logger.Warn($"[{peer.PeerId}] 引导进度写入失败：account='{account}'");
                    return;
                }

                peer.Send(ProtocolId.GuideProgressPush, new GuideProgressPush
                {
                    Mask = merged,
                    ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                server.Logger.Info($"[{peer.PeerId}] 引导进度已同步：account='{account}'，mask={merged}（上报 {request.Mask}）");
            });
        }
    }
}
