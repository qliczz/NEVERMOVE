using System;
using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace NEVERMOVE;

/// <summary>
/// 高亮对象的分类器：判断一个在场玩家属于 好友 / 队友 / 部队成员 中的哪一类。
///
/// 数据来源（全部签名无关、版本稳定，参考 MiniMappingway 的成熟做法）：
///   直接遍历在场对象 ObjectTable，对每个人读原生角色标志位：
///     - 好友：Character-&gt;IsFriend
///     - 队友：Character-&gt;IsPartyMember / IsAllianceMember
///     - 部队：Character-&gt;FreeCompanyTagString 与本机玩家的部队 tag 比较
///   比 InfoProxyFriendList+ContentId 匹配更简洁、更稳、免签名。
///
/// 每 ~250ms 全量扫描一次（开销极小），结果存入 targets 字典供三处高亮读取。
/// </summary>
public sealed class TargetTracker : IDisposable
{
    public enum TargetKind
    {
        Friend = 0,        // 优先级最高
        Party = 1,
        FreeCompany = 2,   // 优先级最低
    }

    private readonly Configuration config;
    private readonly ConcurrentDictionary<ulong, TargetKind> targets = new();
    private DateTime lastScan = DateTime.MinValue;
    private string? localFcTag;
    private ulong localObjectId;

    public TargetTracker(Configuration config) => this.config = config;

    /// <summary>每 ~250ms 全量扫描在场玩家，刷新高亮目标集合（轻量）。</summary>
    public unsafe void Update()
    {
        if ((DateTime.Now - this.lastScan).TotalMilliseconds < 250) return;
        this.lastScan = DateTime.Now;

        try
        {
            var local = Service.ObjectTable.LocalPlayer;
            if (local == null)
            {
                this.targets.Clear();
                return;
            }

            this.localObjectId = local.GameObjectId;
            var localChar = (Character*)local.Address;
            this.localFcTag = localChar->FreeCompanyTagString;

            var fresh = new ConcurrentDictionary<ulong, TargetKind>();
            foreach (var obj in Service.ObjectTable)
            {
                if (obj is not IPlayerCharacter pc) continue;
                if (pc.GameObjectId == this.localObjectId) continue;

                var kind = this.ClassifyNative((Character*)pc.Address);
                if (kind != null) fresh[pc.GameObjectId] = kind.Value;
            }

            // 原子替换，避免绘制线程读到半成品
            this.targets.Clear();
            foreach (var kv in fresh) this.targets[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "[NEVERMOVE] 刷新高亮目标失败（可忽略，下个周期重试）");
        }
    }

    /// <summary>
    /// 给定在场玩家，返回其应被高亮的类别（取优先级最高者）；不属于任何类别返回 null。
    /// 优先级：好友 &gt; 队友 &gt; 部队。
    /// </summary>
    public TargetKind? Classify(IPlayerCharacter pc)
        => this.targets.TryGetValue(pc.GameObjectId, out var k) ? k : null;

    /// <summary>同上，但用 out 参数形式（命中返回 true）。供绘制循环直接使用。</summary>
    public bool Classify(IPlayerCharacter pc, out TargetKind kind)
        => this.targets.TryGetValue(pc.GameObjectId, out kind);

    /// <summary>读原生角色标志位做分类（签名无关）。</summary>
    private unsafe TargetKind? ClassifyNative(Character* c)
    {
        if (c == null) return null;

        if (this.config.EnableFriend && c->IsFriend) return TargetKind.Friend;
        if (this.config.EnableParty && (c->IsPartyMember || c->IsAllianceMember)) return TargetKind.Party;
        if (this.config.EnableFreeCompany &&
            !string.IsNullOrEmpty(this.localFcTag) &&
            this.localFcTag == c->FreeCompanyTagString)
        {
            return TargetKind.FreeCompany;
        }

        return null;
    }

    public void Dispose() => this.targets.Clear();
}
