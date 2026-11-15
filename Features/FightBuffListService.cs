using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Logging;
using Core;
using FairyGUI;
using GameLogic;
using party.model;
using UI;
using UnityEngine;

namespace BattleBuffDisplay.Features;

/// <summary>
/// Two native Common/Com_BuffInfo lists alongside the fight presentation.
/// Uses FightLogic's player IDs and the game's actual BuffContainer filter.
/// Does not replace any game renderer, intercept combat or mutate buffs.
/// </summary>
internal sealed class FightBuffListService : IDisposable {
    private const int FirstCombatStep = 2;
    private const int LastCombatStep = 5;
    private const int MaxBuffsPerSide = 48;
    private const float EnterDuration = .30f;
    private const float ExitDuration = .30f;
    private const float EdgeMargin = 12f;
    private readonly Side _attacker = new Side();
    private readonly Side _defender = new Side();
    private UIFightWindow? _pane;
    private GRoot? _root;
    private GComponent? _overlay;
    private float _exitStartedAt = -1f;
    private FightWindow? _window;
    private long _attackerId;
    private long _defenderId;
    private DateTime _next = DateTime.MinValue;
    private DateTime _warnAt = DateTime.MinValue;
    private bool _typesRegistered;

    internal bool Enabled { get; set; }

    internal void Tick(FightWindow? fight) {
        float now = Time.realtimeSinceStartup;
        if (!Enabled || !GameLogicManager.hasInstance) { Clear(); return; }
        // Animate on EVERY frame; do not tie 0.3s transitions to the 400ms
        // content refresh interval. The root overlay survives a fight popup
        // closing, allowing the short native-style exit animation to finish.
        if (_overlay != null && (_overlay.isDisposed || !ReferenceEquals(_root, GRoot.inst)))
            Clear();
        if (_exitStartedAt >= 0f) {
            if (now - _exitStartedAt >= ExitDuration) Clear();
            else { Place(now); return; }
        }
        try {
            if (fight == null || !fight.isShowing ||
                !(fight.contentPane is UIFightWindow pane) || pane.isDisposed ||
                !pane.onStage || pane.step == null ||
                pane.step.selectedIndex < FirstCombatStep ||
                pane.step.selectedIndex > LastCombatStep) {
                BeginExit(now);
                return;
            }
            if (_overlay != null && (!ReferenceEquals(_pane, pane) ||
                !ReferenceEquals(_window, fight))) Clear();
            Place(now);
            if (DateTime.UtcNow < _next) return;
            _next = DateTime.UtcNow.AddMilliseconds(400);
            GameLogicManager logic = GameLogicManager.inst;
            BattleLogic battle = logic.battle;
            FightLogic fightLogic = logic.fight;
            BattleRole? attack = fightLogic?.attackData;
            BattleRole? defend = fightLogic?.defendData;
            if (battle == null || attack == null || defend == null ||
                attack.PlayerId == defend.PlayerId) { BeginExit(now); return; }
            BattlePlayerData attacker = battle.GetPlayerDataById(attack.PlayerId);
            BattlePlayerData defender = battle.GetPlayerDataById(defend.PlayerId);
            if (attacker?.buffContainer == null || defender?.buffContainer == null) {
                BeginExit(now); return;
            }
            if (_overlay != null && (_attackerId != attack.PlayerId ||
                _defenderId != defend.PlayerId)) Clear();
            if (_overlay == null) {
                _root = GRoot.inst;
                _overlay = new GComponent {
                    name = "BattleBuffDisplay.Overlay", touchable = false, opaque = false
                };
                _overlay.SetSize(_root.width, _root.height);
                _overlay.sortingOrder = Math.Min(32760, pane.sortingOrder + 1);
                _root.AddChild(_overlay);
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
            if (DateTime.UtcNow >= _warnAt) {
                _warnAt = DateTime.UtcNow.AddSeconds(30);
                SdkLog.Warn("BattleBuffDisplay", "战斗 Buff 列表暂不可用：" + ex.Message);
            }
        }
    }

    private void BeginExit(float now) {
        if (_overlay == null || _overlay.isDisposed) { Clear(); return; }
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
        float scale = Math.Min(1f, Math.Min(
            Math.Max(1f, screenWidth * .25f) / Math.Max(1f, panel.width),
            Math.Max(1f, screenHeight - 2f * EdgeMargin) / Math.Max(1f, panel.height)));
        panel.SetScale(scale, scale);
        float width = panel.width * scale;
        float height = panel.height * scale;
        float centerY = screenHeight * .46f;
        float targetY = Math.Max(EdgeMargin, Math.Min(screenHeight - height - EdgeMargin,
            centerY - height * .5f));
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
        // GetShowBuffs applies the game's own hidden/expired filtering.
        var shown = player.buffContainer.GetShowBuffs(player);
        List<Buff> buffs = shown.Item1;
        List<PropertyData<int>> properties = shown.Item2;
        int count = Math.Min(MaxBuffsPerSide, buffs.Count + properties.Count);
        if (count == 0) { side.Clear(); return; }
        if (side.Panel == null || side.Panel.isDisposed || side.Panel.parent != _overlay) {
            side.Clear();
            side.Panel = UICom_BuffInfo.CreateInstance();
            if (side.Panel == null || _overlay == null)
                throw new InvalidOperationException("原版 Buff 卡片尚未就绪");
            _overlay.AddChild(side.Panel);
            side.Panel.touchable = false;
            if (side.Panel.di != null) side.Panel.di.visible = false;
            side.Panel.list_Buff.layout = ListLayoutType.SingleColumn;
            side.Panel.list_Buff.autoResizeItem = false;
            side.Panel.list_Buff.touchable = false;
            side.AppearedAt = now;
        }
        UICom_BuffInfo panel = side.Panel;
        int fingerprint = Fingerprint(buffs, properties, count);
        if (side.Fingerprint == fingerprint) return;
        side.Fingerprint = fingerprint;
        side.Buffs = buffs;
        side.Properties = properties;
        panel.list_Buff.itemRenderer = (index, item) => DrawBuff(side, index, item);
        bool countChanged = panel.list_Buff.numItems != count;
        panel.list_Buff.numItems = count;
        if (!countChanged) {
            for (int i = 0; i < panel.list_Buff.numChildren && i < count; i++)
                DrawBuff(side, i, panel.list_Buff.GetChildAt(i));
        }
        // Match the previous MOD: make panel height depend on the actual
        // cards, then position its center at the same 46% screen-height line.
        // More Buffs grow equally above and below that line.
        panel.list_Buff.EnsureBoundsCorrect();
        float listWidth = 1f;
        float listHeight = 0f;
        for (int i = 0; i < panel.list_Buff.numChildren && i < count; i++) {
            GObject card = panel.list_Buff.GetChildAt(i);
            listWidth = Math.Max(listWidth, card.width);
            listHeight += Math.Max(1f, card.height) + (i == 0 ? 0 : panel.list_Buff.lineGap);
        }
        if (listHeight > 1f) {
            panel.list_Buff.SetSize(listWidth, listHeight);
            panel.list_Buff.SetXY(0f, 0f);
            panel.SetSize(listWidth, listHeight);
        }
    }

    // The former plugin rendered only when buff contents changed, avoiding
    // 2 full UI list rebuilds every update on low-powered machines.
    private static int Fingerprint(List<Buff> buffs, List<PropertyData<int>> properties, int count) {
        unchecked {
            int result = 17 * 31 + count;
            foreach (PropertyData<int> value in properties) {
                result = result * 31 + value.buffId;
                result = result * 31 + value.property.Value;
            }
            foreach (Buff value in buffs) {
                result = result * 31 + value.BuffId;
                result = result * 31 + value.Progress;
                result = result * 31 + value.KeepRound;
            }
            return result;
        }
    }

    private static void DrawBuff(Side side, int index, GObject item) {
        if (!(item is UICom_BuffInfoItem row)) return;
        int id;
        if (index < side.Properties.Count) id = side.Properties[index].buffId;
        else {
            int buffIndex = index - side.Properties.Count;
            if (buffIndex < 0 || buffIndex >= side.Buffs.Count) return;
            id = side.Buffs[buffIndex].BuffId;
        }
        BuffInfoConfigure config = id.GetBuffConfigure();
        if (config == null) return;
        row.loader_Buff.url = config.Icon;
        row.txt_Title.text = config.NameId > 0 ? config.NameId.GetLocal(UIStringType.Buff) : "#" + id;
        int stacks = 0;
        int rounds = 0;
        if (index < side.Properties.Count) stacks = side.Properties[index].property.Value;
        else {
            Buff buff = side.Buffs[index - side.Properties.Count];
            stacks = buff.Progress;
            rounds = buff.KeepRound;
        }
        NativeBuffCardStyle.Style(row, stacks, rounds);
        if (config.DescId > 0) {
            CommonUIManager.RefreshHyperlinkDesc(config.DescId.GetLocal(UIStringType.Buff), row.txt_Desc);
        } else {
            row.txt_Desc.text = "";
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
        _next = DateTime.MinValue;
    }

    public void Dispose() => Clear();

    private sealed class Side {
        internal UICom_BuffInfo? Panel;
        internal List<Buff> Buffs = [];
        internal List<PropertyData<int>> Properties = [];
        internal int Fingerprint = int.MinValue;
        internal float AppearedAt;
        internal float ExitX;

        internal void Clear() {
            if (Panel != null && !Panel.isDisposed) {
                Panel.RemoveFromParent();
                Panel.Dispose();
            }
            Panel = null;
            Fingerprint = int.MinValue;
            AppearedAt = ExitX = 0f;
            // These lists were returned by the game; do not modify them.
            Buffs = new List<Buff>();
            Properties = new List<PropertyData<int>>();
        }
    }
}