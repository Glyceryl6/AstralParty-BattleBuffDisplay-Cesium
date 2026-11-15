using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using Core;
using FairyGUI;
using GameLogic;
using party.model;
using UI;
using UnityEngine;

namespace BattleBuffDisplay.Features;

/// <summary>
/// Displays both sides' Buffs using the game's original
/// BattlePlayerInfoWindow.RefreshMonsterBuffList renderer. The MOD only adds
/// the card background/counters and positions the two original lists.
/// </summary>
internal sealed class FightBuffListService : IDisposable {
    private const int FirstCombatStep = 2;
    private const int LastCombatStep = 5;
    private const int MaxBuffsPerSide = 48;
    private const float RefreshInterval = 1.10f;
    private const float PhaseCheckInterval = .08f;
    private const float ExitGrace = .45f;
    private const float EnterDuration = .30f;
    private const float ExitDuration = .30f;
    private const float EdgeMargin = 12f;

    private static readonly MethodInfo? NativeListRenderer = typeof(BattlePlayerInfoWindow).GetMethod(
        "RefreshMonsterBuffList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly Side _attacker = new Side();
    private readonly Side _defender = new Side();
    private UIFightWindow? _pane;
    private FightWindow? _window;
    private GRoot? _root;
    private GComponent? _overlay;
    private float _exitStartedAt = -1f;
    private float _nonCombatSince = -1f;
    private float _nextPollAt;
    private float _nextPhaseCheckAt;
    private float _warnAt;
    private long _attackerId;
    private long _defenderId;
    private bool _typesRegistered;

    internal bool Enabled { get; set; }

    internal void Tick(FightWindow? fight) {
        float now = Time.realtimeSinceStartup;
        if (!Enabled || !GameLogicManager.hasInstance) {
            Clear();
            return;
        }

        if (_overlay != null && (_overlay.isDisposed || !ReferenceEquals(_root, GRoot.inst)))
            Clear();

        try {
            bool inCombat = IsCombatWindow(fight, out UIFightWindow? pane);

            // A FightWindow/content pane can be swapped for a frame during the
            // ready/result transitions. Do not play exit/enter twice for that
            // transient state; only exit after it has remained non-combat.
            if (_overlay != null && now >= _nextPhaseCheckAt) {
                _nextPhaseCheckAt = now + PhaseCheckInterval;
                if (inCombat) {
                    _nonCombatSince = -1f;
                } else {
                    if (_nonCombatSince < 0f) _nonCombatSince = now;
                    if (_exitStartedAt < 0f && now - _nonCombatSince >= ExitGrace)
                        BeginExit(now);
                }
            }

            if (_exitStartedAt >= 0f) {
                if (inCombat) {
                    // A transient window change ended before the exit finished.
                    // Keep the cards visible instead of replaying a full entry.
                    _exitStartedAt = -1f;
                    _nonCombatSince = -1f;
                    _attacker.AppearedAt = now - EnterDuration;
                    _defender.AppearedAt = now - EnterDuration;
                } else {
                    if (now - _exitStartedAt >= ExitDuration) Clear();
                    else Place(now);
                    return;
                }
            }

            if (!inCombat || pane == null || fight == null) {
                if (_overlay != null) Place(now);
                return;
            }

            if (_overlay != null && (!ReferenceEquals(_pane, pane) || !ReferenceEquals(_window, fight))) {
                // Rebind to the active fight window without tearing down the
                // cards. FightWindow may replace its content pane mid-transition.
                _pane = pane;
                _window = fight;
                if (_overlay != null && !_overlay.isDisposed)
                    _overlay.sortingOrder = Math.Min(32760, pane.sortingOrder + 1);
            }

            if (now < _nextPollAt) {
                Place(now);
                return;
            }
            _nextPollAt = now + RefreshInterval;

            GameLogicManager logic = GameLogicManager.inst;
            BattleLogic? battle = logic.battle;
            FightLogic? fightLogic = logic.fight;
            BattleRole? attack = fightLogic?.attackData;
            BattleRole? defend = fightLogic?.defendData;
            // The combat controller can be present one or two frames before
            // the server's attacker/defender data. This is not an exit state.
            if (battle == null || attack == null || defend == null ||
                attack.PlayerId == defend.PlayerId) {
                Place(now);
                return;
            }

            BattlePlayerData? attacker = battle.GetPlayerDataById(attack.PlayerId);
            BattlePlayerData? defender = battle.GetPlayerDataById(defend.PlayerId);
            if (attacker?.buffContainer == null || defender?.buffContainer == null) {
                Place(now);
                return;
            }

            if (_overlay != null && (_attackerId != attack.PlayerId || _defenderId != defend.PlayerId)) {
                _attacker.Clear();
                _defender.Clear();
            }

            if (_overlay == null) CreateOverlay(pane, fight, attack.PlayerId, defend.PlayerId);
            else {
                _pane = pane;
                _window = fight;
                _attackerId = attack.PlayerId;
                _defenderId = defend.PlayerId;
            }

            RegisterNativeItems();
            Render(_attacker, attacker, now);
            Render(_defender, defender, now);
            Place(now);
        } catch (Exception ex) {
            Clear();
            if (now >= _warnAt) {
                _warnAt = now + 30f;
                SdkLog.Warn("BattleBuffDisplay", "原版 Buff 列表渲染失败，已安全跳过本次更新：" + ex);
            }
        }
    }

    private static bool IsCombatWindow(FightWindow? fight, out UIFightWindow? pane) {
        pane = null;
        if (fight == null || !fight.isShowing ||
            !(fight.contentPane is UIFightWindow activePane) || activePane.isDisposed ||
            !activePane.onStage || activePane.step == null) return false;
        int phase = activePane.step.selectedIndex;
        if (phase < FirstCombatStep || phase > LastCombatStep) return false;
        pane = activePane;
        return true;
    }

    private void CreateOverlay(UIFightWindow pane, FightWindow fight, long attackerId, long defenderId) {
        _root = GRoot.inst;
        if (_root == null) throw new InvalidOperationException("FairyGUI 根节点尚未就绪");
        _overlay = new GComponent {
            name = "BattleBuffDisplay.Overlay",
            touchable = false,
            opaque = false
        };
        _overlay.SetSize(_root.width, _root.height);
        _overlay.sortingOrder = Math.Min(32760, pane.sortingOrder + 1);
        _root.AddChild(_overlay);
        _pane = pane;
        _window = fight;
        _attackerId = attackerId;
        _defenderId = defenderId;
        _nextPhaseCheckAt = Time.realtimeSinceStartup + PhaseCheckInterval;
        _nonCombatSince = -1f;
    }

    private void BeginExit(float now) {
        if (_overlay == null || _overlay.isDisposed) {
            Clear();
            return;
        }
        if (_exitStartedAt >= 0f) return;
        _exitStartedAt = now;
        _attacker.ExitX = _attacker.Panel?.x ?? 0f;
        _defender.ExitX = _defender.Panel?.x ?? 0f;
        Place(now);
    }

    private void Place(float now) {
        if (_overlay == null || _overlay.isDisposed || _root == null) return;
        _overlay.SetSize(_root.width, _root.height);
        PlaceSide(_attacker, false, now);
        PlaceSide(_defender, true, now);
    }

    private void PlaceSide(Side side, bool right, float now) {
        UICom_BuffInfo? panel = side.Panel;
        if (panel == null || panel.isDisposed || !panel.visible || _root == null) return;
        float screenWidth = _root.width;
        float screenHeight = _root.height;
        float width = panel.width;
        float height = panel.height;
        float centerY = screenHeight * .46f;
        float maxY = Math.Max(EdgeMargin, screenHeight - height - EdgeMargin);
        float targetY = Math.Max(EdgeMargin, Math.Min(maxY, centerY - height * .5f));
        float targetX = right ? Math.Max(EdgeMargin, screenWidth - width - EdgeMargin) : EdgeMargin;
        float outsideX = right ? screenWidth + 16f : -width - 16f;
        float x;
        float alpha;
        if (_exitStartedAt >= 0f) {
            float t = Smooth((now - _exitStartedAt) / ExitDuration);
            x = side.ExitX + (outsideX - side.ExitX) * t;
            alpha = 1f - t;
        } else {
            float t = Smooth((now - side.AppearedAt) / EnterDuration);
            x = outsideX + (targetX - outsideX) * t;
            alpha = .2f + .8f * t;
        }
        panel.SetScale(1f, 1f);
        panel.SetXY(x, targetY);
        panel.alpha = alpha;
    }

    private static float Smooth(float t) {
        t = Math.Max(0f, Math.Min(1f, t));
        return t * t * (3f - 2f * t);
    }

    private void RegisterNativeItems() {
        if (_typesRegistered) return;
        UIObjectFactory.SetPackageItemExtension(UICom_BuffInfo.URL, typeof(UICom_BuffInfo));
        UIObjectFactory.SetPackageItemExtension(UICom_BuffInfoItem.URL, typeof(UICom_BuffInfoItem));
        _typesRegistered = true;
    }

    private void Render(Side side, BattlePlayerData player, float now) {
        // Keep the same order as the original renderer: property Buffs first,
        // then normal Buffs. Cap the display without modifying game-owned lists.
        var shown = player.buffContainer.GetShowBuffs(player);
        List<PropertyData<int>> properties = new List<PropertyData<int>>();
        List<Buff> buffs = new List<Buff>();
        int propertyCount = Math.Min(MaxBuffsPerSide, shown.Item2.Count);
        for (int i = 0; i < propertyCount; i++) properties.Add(shown.Item2[i]);
        int buffCount = Math.Min(MaxBuffsPerSide - propertyCount, shown.Item1.Count);
        for (int i = 0; i < buffCount; i++) buffs.Add(shown.Item1[i]);
        int count = properties.Count + buffs.Count;

        if (count == 0) {
            if (side.Panel != null && !side.Panel.isDisposed) side.Panel.visible = false;
            side.Buffs = buffs;
            side.Properties = properties;
            side.Fingerprint = int.MinValue;
            side.AppearedAt = -1f;
            return;
        }

        bool wasHidden = side.Panel == null || side.Panel.isDisposed || !side.Panel.visible;
        if (side.Panel == null || side.Panel.isDisposed || side.Panel.parent != _overlay) {
            side.Clear();
            side.Panel = UICom_BuffInfo.CreateInstance();
            if (side.Panel == null || _overlay == null)
                throw new InvalidOperationException("原版 Common/Com_BuffInfo 组件尚未就绪");
            _overlay.AddChild(side.Panel);
            side.Panel.touchable = false;
            if (side.Panel.di != null) side.Panel.di.visible = false;
            GList list = side.Panel.list_Buff;
            list.layout = ListLayoutType.SingleColumn;
            list.lineCount = 1;
            list.columnGap = list.lineGap;
            list.autoResizeItem = false;
            list.touchable = false;
            wasHidden = true;
        }

        int fingerprint = Fingerprint(buffs, properties, count);
        if (!wasHidden && side.Fingerprint == fingerprint) return;

        side.Buffs = buffs;
        side.Properties = properties;
        RefreshNativeList(side.Panel!, side.Buffs, side.Properties);
        side.Fingerprint = fingerprint;
        side.Panel!.visible = true;
        if (wasHidden || side.AppearedAt < 0f) side.AppearedAt = now;
        MeasureAndStyle(side);
    }

    private static void RefreshNativeList(UICom_BuffInfo panel,
        List<Buff> buffs, List<PropertyData<int>> properties) {
        MethodInfo? renderer = NativeListRenderer;
        if (renderer == null)
            throw new MissingMethodException("BattlePlayerInfoWindow.RefreshMonsterBuffList");
        // This is the game's own itemRenderer. It fills the same native icon,
        // title and rich description fields as the original battle info panel.
        object? host = GetMember(UIManager.inst, "battlePlayerInfo");
        if (host == null)
            throw new InvalidOperationException("UIManager.battlePlayerInfo 尚未创建");
        renderer.Invoke(host, new object[] { panel, buffs, properties });
        panel.list_Buff.EnsureBoundsCorrect();
    }

    private static object? GetMember(object instance, string name) {
        for (Type? type = instance.GetType(); type != null; type = type.BaseType) {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            FieldInfo? field = type.GetField(name, flags);
            if (field != null) return field.GetValue(instance);
            PropertyInfo? property = type.GetProperty(name, flags);
            if (property != null) return property.GetValue(instance, null);
        }
        return null;
    }

    private static void MeasureAndStyle(Side side) {
        UICom_BuffInfo? panel = side.Panel;
        if (panel == null || panel.isDisposed) return;
        GList list = panel.list_Buff;
        list.EnsureBoundsCorrect();
        int actual = Math.Min(list.numItems, list.numChildren);
        float largestWidth = 1f;
        float totalHeight = 0f;
        float gap = Math.Max(0f, list.lineGap);
        for (int i = 0; i < actual; i++) {
            if (!(list.GetChildAt(i) is UICom_BuffInfoItem row)) continue;
            GetCounterValues(side, i, out int progress, out int rounds);
            NativeBuffCardStyle.Style(row, progress, rounds);
            row.EnsureBoundsCorrect();
            largestWidth = Math.Max(largestWidth, row.width);
            if (totalHeight > 0f) totalHeight += gap;
            totalHeight += row.height;
        }
        if (actual <= 0) return;
        list.SetXY(0f, 0f);
        list.SetSize(largestWidth, totalHeight);
        list.EnsureBoundsCorrect();
        panel.SetSize(largestWidth, totalHeight);
    }

    private static void GetCounterValues(Side side, int index, out int progress, out int rounds) {
        progress = 0;
        rounds = 0;
        if (index < side.Properties.Count) {
            PropertyData<int>? value = side.Properties[index];
            progress = value?.property == null ? 0 : value.property.Value;
            return;
        }
        int buffIndex = index - side.Properties.Count;
        if (buffIndex < 0 || buffIndex >= side.Buffs.Count) return;
        Buff? buff = side.Buffs[buffIndex];
        if (buff == null) return;
        progress = buff.Progress;
        rounds = buff.KeepRound;
    }

    private static int Fingerprint(List<Buff> buffs, List<PropertyData<int>> properties, int count) {
        unchecked {
            int result = 17 * 31 + count;
            foreach (PropertyData<int> value in properties) {
                result = result * 31 + (value?.buffId ?? 0);
                result = result * 31 + (value?.property == null ? 0 : value.property.Value);
            }
            foreach (Buff value in buffs) {
                result = result * 31 + (value?.BuffId ?? 0);
                result = result * 31 + (value?.Progress ?? 0);
                result = result * 31 + (value?.KeepRound ?? 0);
            }
            return result;
        }
    }

    internal void Clear() {
        _attacker.Clear();
        _defender.Clear();
        if (_overlay != null && !_overlay.isDisposed) {
            _overlay.RemoveFromParent();
            _overlay.Dispose();
        }
        _overlay = null;
        _root = null;
        _pane = null;
        _window = null;
        _attackerId = _defenderId = 0;
        _exitStartedAt = -1f;
        _nonCombatSince = -1f;
        _nextPollAt = 0f;
        _nextPhaseCheckAt = 0f;
    }

    public void Dispose() => Clear();

    private sealed class Side {
        internal UICom_BuffInfo? Panel;
        internal List<Buff> Buffs = new List<Buff>();
        internal List<PropertyData<int>> Properties = new List<PropertyData<int>>();
        internal int Fingerprint = int.MinValue;
        internal float AppearedAt = -1f;
        internal float ExitX;

        internal void Clear() {
            if (Panel != null && !Panel.isDisposed) {
                Panel.RemoveFromParent();
                Panel.Dispose();
            }
            Panel = null;
            Fingerprint = int.MinValue;
            AppearedAt = -1f;
            ExitX = 0f;
            Buffs = new List<Buff>();
            Properties = new List<PropertyData<int>>();
        }
    }
}
