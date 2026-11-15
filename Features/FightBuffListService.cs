using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Logging;
using Core;
using FairyGUI;
using GameLogic;
using party.model;
using UI;

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
    private readonly Side _attacker = new Side();
    private readonly Side _defender = new Side();
    private UIFightWindow? _pane;
    private FightWindow? _window;
    private long _attackerId;
    private long _defenderId;
    private DateTime _next = DateTime.MinValue;
    private DateTime _warnAt = DateTime.MinValue;
    private bool _typesRegistered;

    internal bool Enabled { get; set; }

    internal void Tick() {
        if (!Enabled || !UIManager.hasInstance || !GameLogicManager.hasInstance) {
            Clear();
            return;
        }
        if (DateTime.UtcNow < _next) return;
        _next = DateTime.UtcNow.AddMilliseconds(400);
        try {
            UIManager manager = UIManager.inst;
            FightWindow fight = manager.Fight;
            if (!fight.isShowing || !(fight.contentPane is UIFightWindow pane) ||
                pane.isDisposed || pane.parent == null || pane.step == null ||
                pane.step.selectedIndex < FirstCombatStep || pane.step.selectedIndex > LastCombatStep) {
                Clear();
                return;
            }
            GameLogicManager logic = GameLogicManager.inst;
            BattleLogic battle = logic.battle;
            FightLogic fightLogic = logic.fight;
            BattleRole? attack = fightLogic?.attackData;
            BattleRole? defend = fightLogic?.defendData;
            if (battle == null || attack == null || defend == null ||
                attack.PlayerId == defend.PlayerId) { Clear(); return; }
            BattlePlayerData attacker = battle.GetPlayerDataById(attack.PlayerId);
            BattlePlayerData defender = battle.GetPlayerDataById(defend.PlayerId);
            if (attacker?.buffContainer == null || defender?.buffContainer == null) { Clear(); return; }
            if (!ReferenceEquals(_pane, pane) || !ReferenceEquals(_window, fight) ||
                _attackerId != attack.PlayerId || _defenderId != defend.PlayerId) {
                Clear();
                _pane = pane;
                _window = fight;
                _attackerId = attack.PlayerId;
                _defenderId = defend.PlayerId;
            }
            RegisterNativeItems();
            Render(_attacker, pane, attacker, true);
            Render(_defender, pane, defender, false);
        } catch (Exception e) {
            Clear();
            if (DateTime.UtcNow >= _warnAt) {
                _warnAt = DateTime.UtcNow.AddSeconds(30);
                SdkLog.Warn("BattleBuffDisplay", "战斗 Buff 列表暂不可用：" + e.Message);
            }
        }
    }

    private void RegisterNativeItems() {
        if (_typesRegistered) return;
        UIObjectFactory.SetPackageItemExtension(UICom_BuffInfo.URL, typeof(UICom_BuffInfo));
        UIObjectFactory.SetPackageItemExtension(UICom_BuffInfoItem.URL, typeof(UICom_BuffInfoItem));
        _typesRegistered = true;
    }

    private static void Render(Side side, UIFightWindow pane, BattlePlayerData player, bool left) {
        // GetShowBuffs filters hidden and expired buffs, exactly as the
        // game's own BattlePlayerInfoWindow does. Tuple lists are held by
        // the Side until the next refresh so itemRenderer has live data.
        var shown = player.buffContainer.GetShowBuffs(player);
        List<Buff> buffs = shown.Item1;
        List<PropertyData<int>> properties = shown.Item2;
        int count = Math.Min(MaxBuffsPerSide, buffs.Count + properties.Count);
        if (count == 0) { side.Clear(); return; }
        if (side.Panel == null || side.Panel.isDisposed || side.Panel.parent != pane) {
            side.Clear();
            side.Panel = UICom_BuffInfo.CreateInstance();
            pane.AddChild(side.Panel);
            side.Panel.touchable = false;
        }
        UICom_BuffInfo panel = side.Panel;
        float naturalWidth = Math.Max(1f, panel.width);
        float naturalHeight = Math.Max(1f, panel.height);
        float spaceWidth = Math.Max(96f, pane.width * .235f);
        float spaceHeight = Math.Max(80f, pane.height * .69f);
        float scale = Math.Min(1f, Math.Min(spaceWidth / naturalWidth, spaceHeight / naturalHeight));
        panel.SetScale(scale, scale);
        float actualWidth = naturalWidth * scale;
        panel.SetXY(left ? 8f : Math.Max(8f, pane.width - actualWidth - 8f), 44f);
        side.Buffs = buffs;
        side.Properties = properties;
        panel.list_Buff.itemRenderer = (index, item) => DrawBuff(side, index, item);
        panel.list_Buff.numItems = count;
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
        _pane = null;
        _window = null;
        _attackerId = _defenderId = 0;
    }

    public void Dispose() => Clear();

    private sealed class Side {
        internal UICom_BuffInfo? Panel;
        internal List<Buff> Buffs = [];
        internal List<PropertyData<int>> Properties = [];

        internal void Clear() {
            if (Panel != null && !Panel.isDisposed) {
                Panel.RemoveFromParent();
                Panel.Dispose();
            }
            Panel = null;
            // These lists were returned by the game; do not modify them.
            Buffs = new List<Buff>();
            Properties = new List<PropertyData<int>>();
        }
    }
}