using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using Core;
using FairyGUI;
using GameLogic;
using party.model;
using UI;
using Tools;
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
    private const float ExitGrace = .12f;
    private const float EnterDuration = .30f;
    private const float DefaultExitDuration = .30f;
    private const float EdgeMargin = 12f;

    private static readonly MethodInfo? NativeListRenderer = typeof(BattlePlayerInfoWindow).GetMethod(
        "RefreshMonsterBuffList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    // The original FightWindow plays fightOver forwards when FinishFight runs,
    // and reverses it when closing. Both mark the end of the current round.
    private static readonly FieldInfo? TransitionDuration = typeof(Transition).GetField(
        "_totalDuration", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Side _attacker = new Side();
    private readonly Side _defender = new Side();
    private UIFightWindow? _pane;
    private FightWindow? _window;
    private GRoot? _root;
    private GComponent? _overlay;
    private float _exitStartedAt = -1f;
    private float _exitDuration = DefaultExitDuration;
    private bool _awaitNextRound;
    private bool _newRoundRequested;
    private bool _resumeAfterExit;
    private bool _introPending;
    private bool _introSeen;
    private bool _combatLive;
    private float _readyAt;
    internal bool CombatLive => _combatLive;
    private Signal<bool, UIPanelType>? _readySignal;
    private float _nonCombatSince = -1f;
    private float _nextPollAt;
    private float _warnAt;
    private long _attackerId;
    private long _defenderId;
    private bool _typesRegistered;

    internal bool Enabled { get; set; }

    // The native ReadyFight signal is sent after the next pair of actors is
    // assigned, including a counterattack. Window visibility cannot identify
    // this boundary: the same FightWindow stays open between both rounds.
    private void OnReadyFight(bool isReady, UIPanelType panel) {
        if (!isReady) return;
        // ReadyFight changes step to 2 BEFORE the native intro movie plays.
        // The current combat is not ready for Buff cards/effects at this point.
        _newRoundRequested = true;
        _introPending = true;
        _introSeen = false;
        _readyAt = Time.realtimeSinceStartup;
        _combatLive = false;
    }

    private void EnsureRoundSignal() {
        Signal<bool, UIPanelType>? signal = GameLogicManager.inst.fight?.signal.readyFight;
        if (ReferenceEquals(signal, _readySignal)) return;
        if (_readySignal != null) _readySignal.RemoveListener(OnReadyFight);
        _readySignal = signal;
        if (_readySignal != null) _readySignal.AddListener(OnReadyFight);
    }

    internal void Tick(FightWindow? fight) {
        float now = Time.realtimeSinceStartup;
        if (!Enabled || !GameLogicManager.hasInstance) {
            Clear();
            return;
        }

        if (_overlay != null && (_overlay.isDisposed || !ReferenceEquals(_root, GRoot.inst)))
            ClearVisuals();

        try {
            EnsureRoundSignal();
            if (_newRoundRequested) {
                _newRoundRequested = false;
                // A counterattack reuses the same FightWindow. Let the old
                // actors retract before releasing their cards and effects.
                if (_overlay != null) {
                    _resumeAfterExit = true;
                    BeginExit(now, DefaultExitDuration);
                    _awaitNextRound = true;
                } else {
                    _resumeAfterExit = false;
                    _awaitNextRound = false;
                }
            }

            bool inCombat = IsCombatWindow(fight, out UIFightWindow? pane);
            bool windowVisible = (fight != null && fight.isShowing) ||
                (_window != null && _window.isShowing) || IsFightWindowShowing();
            UIFightWindow? nativePane = (fight ?? _window)?.contentPane as UIFightWindow;
            int phase = nativePane?.step?.selectedIndex ?? -1;
            bool movieVisible = nativePane?.com_Show?.loader_Movie?.visible == true;
            bool fightOverPlaying = TryGetFightOverDuration(nativePane, out float nativeDuration);
            if (movieVisible) _introSeen = true;
            if (_introPending) {
                // Some replay paths omit the movie: use a bounded fallback,
                // but never draw while an actual intro is still on screen.
                if (!movieVisible && (_introSeen || now - _readyAt >= 1.0f))
                    _introPending = false;
            }

            // FinishFight.Play() is the beginning of the old MOD's exit,
            // not Hide() and not only the later PlayReverse(). Result phase
            // and truly closed windows also end this combat round.
            if (_overlay != null && !_awaitNextRound) {
                bool ended = fightOverPlaying || phase == 6;
                if (!windowVisible) {
                    if (_nonCombatSince < 0f) _nonCombatSince = now;
                    ended |= now - _nonCombatSince >= ExitGrace;
                } else _nonCombatSince = -1f;
                if (ended) {
                    BeginExit(now, fightOverPlaying
                        ? nativeDuration : DefaultExitDuration);
                    _awaitNextRound = true;
                }
            }

            if (_awaitNextRound) {
                if (_overlay != null) {
                    if (_exitStartedAt < 0f) BeginExit(now, DefaultExitDuration);
                    if (now - _exitStartedAt >= _exitDuration) ClearVisuals();
                    else Place(now);
                    return;
                }
                // A completed fight still has step=2 until its native
                // window hides. Only readyFight may restart the display.
                if (!_resumeAfterExit) return;
                _resumeAfterExit = false;
                _awaitNextRound = false;
            }

            if (!inCombat || pane == null || fight == null || fightOverPlaying ||
                movieVisible || _introPending) {
                _combatLive = false;
                if (_overlay != null) Place(now);
                return;
            }

            if (_overlay != null && (!ReferenceEquals(_pane, pane) || !ReferenceEquals(_window, fight))) {
                // Different UI instances cannot share old Buff card data.
                ClearVisuals();
            }

            if (now < _nextPollAt) {
                _combatLive = _overlay != null && _exitStartedAt < 0f;
                Place(now);
                return;
            }
            _nextPollAt = now + RefreshInterval;

            GameLogicManager logic = GameLogicManager.inst;
            BattleLogic? battle = logic.battle;
            // Old MOD reads FightWindow._AttackerData/_DefenderData, not the
            // FightLogic state (which may still refer to another round during
            // the first frames). Wait for the displayed window's own actors.
            BattlePlayerData? windowAttacker = GetMember(fight, "_AttackerData") as BattlePlayerData;
            BattlePlayerData? windowDefender = GetMember(fight, "_DefenderData") as BattlePlayerData;
            long attackerId = windowAttacker?.player?.Id ?? 0;
            long defenderId = windowDefender?.player?.Id ?? 0;
            // The combat controller can be present one or two frames before
            // the server's attacker/defender data. This is not an exit state.
            if (battle == null || attackerId <= 0 || defenderId <= 0 ||
                attackerId == defenderId) {
                Place(now);
                return;
            }

            BattlePlayerData? attacker = windowAttacker;
            BattlePlayerData? defender = windowDefender;
            if (attacker?.buffContainer == null || defender?.buffContainer == null) {
                Place(now);
                return;
            }

            if (_overlay != null && (_attackerId != attackerId || _defenderId != defenderId)) {
                _attacker.Clear();
                _defender.Clear();
            }

            if (_overlay == null) CreateOverlay(pane, fight, attackerId, defenderId);
            else {
                _pane = pane;
                _window = fight;
                _attackerId = attackerId;
                _defenderId = defenderId;
            }

            RegisterNativeItems();
            Render(_attacker, attacker, now);
            Render(_defender, defender, now);
            _combatLive = true;
            Place(now);
        } catch (Exception ex) {
            Clear();
            if (now >= _warnAt) {
                _warnAt = now + 30f;
                SdkLog.Warn("BattleBuffDisplay", "原版 Buff 列表渲染失败，已安全跳过本次更新：" + ex);
            }
        }
    }

    private static bool IsFightWindowShowing() {
        if (!UIManager.hasInstance) return false;
        foreach (BaseWindow popup in UIManager.inst.propUpWindows) {
            if (popup is FightWindow window && window.isShowing) return true;
        }
        return false;
    }

    private static bool TryGetFightOverDuration(UIFightWindow? pane, out float duration) {
        duration = DefaultExitDuration;
        try {
            Transition? transition = pane?.com_Show?.fightOver;
            if (transition == null || !transition.playing) return false;
            if (TransitionDuration?.GetValue(transition) is float native && native > .05f) {
                float scale = Math.Max(.01f, Math.Abs(transition.timeScale));
                duration = Math.Max(.18f, Math.Min(2f, native / scale));
            }
            return true;
        } catch { return false; }
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
        _nonCombatSince = -1f;
    }

    private void BeginExit(float now, float duration) {
        if (_overlay == null || _overlay.isDisposed) {
            Clear();
            return;
        }
        if (_exitStartedAt >= 0f) return;
        _exitStartedAt = now;
        _combatLive = false;
        _exitDuration = duration;
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
            float t = Smooth((now - _exitStartedAt) / _exitDuration);
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
            // GetShowBuffs can briefly return an empty snapshot during a fight
            // phase transition. Keep the last rendered cards until the empty
            // result is stable rather than flashing them out and back in.
            if (side.Panel != null && !side.Panel.isDisposed && side.Panel.visible) {
                if (side.EmptySince < 0f) side.EmptySince = now;
                if (now - side.EmptySince < RefreshInterval + .15f) return;
            }
            if (side.Panel != null && !side.Panel.isDisposed) side.Panel.visible = false;
            side.Buffs = buffs;
            side.Properties = properties;
            side.Fingerprint = int.MinValue;
            side.AppearedAt = -1f;
            return;
        }

        side.EmptySince = -1f;
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

    private void ClearVisuals() {
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
        _combatLive = false;
        _exitDuration = DefaultExitDuration;
        _nonCombatSince = -1f;
        _nextPollAt = 0f;
    }

    internal void Clear() {
        ClearVisuals();
        _awaitNextRound = false;
        _newRoundRequested = false;
        _resumeAfterExit = false;
        _introPending = false;
        _introSeen = false;
        _readyAt = 0f;
        if (_readySignal != null) {
            _readySignal.RemoveListener(OnReadyFight);
            _readySignal = null;
        }
    }

    public void Dispose() => Clear();

    private sealed class Side {
        internal UICom_BuffInfo? Panel;
        internal List<Buff> Buffs = new List<Buff>();
        internal List<PropertyData<int>> Properties = new List<PropertyData<int>>();
        internal int Fingerprint = int.MinValue;
        internal float AppearedAt = -1f;
        internal float ExitX;
        internal float EmptySince = -1f;

        internal void Clear() {
            if (Panel != null && !Panel.isDisposed) {
                Panel.RemoveFromParent();
                Panel.Dispose();
            }
            Panel = null;
            Fingerprint = int.MinValue;
            AppearedAt = -1f;
            ExitX = 0f;
            EmptySince = -1f;
            Buffs = new List<Buff>();
            Properties = new List<PropertyData<int>>();
        }
    }
}
