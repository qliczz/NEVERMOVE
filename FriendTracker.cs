using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

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
    private volatile IReadOnlyDictionary<ulong, TargetKind> targets = new Dictionary<ulong, TargetKind>();
    private readonly Dictionary<ulong, byte> appliedOutlines = new();
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
                this.targets = new Dictionary<ulong, TargetKind>();
                return;
            }

            this.localObjectId = local.GameObjectId;
            var localChar = (Character*)local.Address;
            this.localFcTag = localChar->FreeCompanyTagString;

            // 区域限制：仅大世界启用；副本/地牢/绝境战/团队(BoundByDuty) 或 危命任务(FateId!=0) 时清空高亮目标
            if (this.config.OnlyOpenWorld && this.IsRestricted(local))
            {
                this.targets = new Dictionary<ulong, TargetKind>();
                return;
            }

            var fresh = new Dictionary<ulong, TargetKind>();
            foreach (var obj in Service.ObjectTable)
            {
                if (obj is not IPlayerCharacter pc) continue;
                if (pc.GameObjectId == this.localObjectId) continue;

                var kind = this.ClassifyNative((Character*)pc.Address);
                if (kind != null) fresh[pc.GameObjectId] = kind.Value;
            }

            // 一次替换完整快照，绘制侧不会看到 Clear 与回填之间的半成品。
            this.targets = fresh;
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

    /// <summary>是否处于应禁用的场景：副本/地牢/绝境战/团队（BoundByDuty）或 危命任务（FateId!=0）。</summary>
    private unsafe bool IsRestricted(IPlayerCharacter local)
    {
        try
        {
            if (Service.Condition[ConditionFlag.BoundByDuty]) return true;
        }
        catch
        {
            // Condition 不可用时不视为限制
        }

        try
        {
            if (((GameObject*)local.Address)->FateId != 0) return true;
        }
        catch
        {
            // 读取失败不视为限制
        }

        return false;
    }

    /// <summary>
    /// 每帧调用：对被高亮目标施加游戏原生「选中目标」轮廓光效（Highlight）。
    /// 仅在状态/颜色变化时调用对应对象，避免每帧重触发闪烁；不再满足条件的对象清除描边。
    /// 关闭或卸载时由 ClearApplied 全部清除。
    /// </summary>
    public unsafe void ApplyOutlines()
    {
        if (!this.config.EnableOutline)
        {
            this.ClearApplied();
            return;
        }

        try
        {
            var local = Service.ObjectTable.LocalPlayer;
            var localId = local?.GameObjectId ?? 0;
            var present = new HashSet<ulong>();

            foreach (var obj in Service.ObjectTable)
            {
                if (obj is not IPlayerCharacter pc) continue;
                if (pc.GameObjectId == localId) continue;

                if (this.Classify(pc, out var kind))
                {
                    var color = this.OutlineColorFor(kind);
                    present.Add(pc.GameObjectId);
                    if (!this.appliedOutlines.TryGetValue(pc.GameObjectId, out var cur) || cur != color)
                    {
                        ((GameObject*)pc.Address)->Highlight((ObjectHighlightColor)color);
                        this.appliedOutlines[pc.GameObjectId] = color;
                    }
                }
                else if (this.appliedOutlines.TryGetValue(pc.GameObjectId, out _))
                {
                    ((GameObject*)pc.Address)->Highlight(ObjectHighlightColor.None);
                    this.appliedOutlines.Remove(pc.GameObjectId);
                }
            }

            // 忘记已离场的对象（其 Highlight 会随对象销毁自动消失，无需再调用）
            foreach (var id in this.appliedOutlines.Keys.ToList())
            {
                if (!present.Contains(id)) this.appliedOutlines.Remove(id);
            }
        }
        catch (Exception ex)
        {
            Service.Log.Debug(ex, "[NEVERMOVE] 描边光效应用失败（可忽略）");
        }
    }

    private byte OutlineColorFor(TargetKind kind) => kind switch
    {
        TargetKind.Friend => this.config.OutlineColorFriend,
        TargetKind.Party => this.config.OutlineColorParty,
        TargetKind.FreeCompany => this.config.OutlineColorFreeCompany,
        _ => this.config.OutlineColorFriend,
    };

    /// <summary>清除所有已施加的描边（关闭插件 / 切换场景 / 卸载时调用）。</summary>
    private unsafe void ClearApplied()
    {
        foreach (var id in this.appliedOutlines.Keys.ToList())
        {
            var obj = this.FindObject(id);
            if (obj != null)
            {
                try { ((GameObject*)obj.Address)->Highlight(ObjectHighlightColor.None); } catch { }
            }
        }

        this.appliedOutlines.Clear();
    }

    private IPlayerCharacter? FindObject(ulong id)
    {
        foreach (var obj in Service.ObjectTable)
        {
            if (obj is IPlayerCharacter pc && pc.GameObjectId == id) return pc;
        }

        return null;
    }

    public void Dispose()
    {
        this.ClearApplied();
        this.targets = new Dictionary<ulong, TargetKind>();
    }
}
