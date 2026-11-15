using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Logging;
using Core;
using Core.Scene;
using Core.Unit;
using GameLogic;
using party.model;
using UI;
using UnityEngine;

namespace BattleBuffDisplay.Features;

/// <summary>
/// Mirrors only Buff effects omitted by the game's PK renderer. Native
/// BattleActor.CreateBuffEffect already handles IsShowEffectDuringPK; using
/// EffectManager.PlayById for the remainder avoids manual Unity instantiation
/// and the legacy IL2CppSharp / native-pointer bridges entirely.
/// </summary>
internal sealed class FightBuffVisualService : IDisposable {
    private const int MaxCopies = 48;
    private readonly Dictionary<string, EffectHandle> _effects = new();
    private readonly HashSet<string> _seen = new();
    private BattleShowDirector? _director;
    private FightWindow? _fightWindow;
    private BattleActor? _attacker;
    private BattleActor? _defender;
    private DateTime _next = DateTime.MinValue;
    private DateTime _warnAt = DateTime.MinValue;
    private int _generation;

    internal bool Enabled { get; set; }

    internal void Tick() {
        if (!Enabled || !UIManager.hasInstance || !GameLogicManager.hasInstance ||
            !EffectManager.hasInstance) { Clear(); return; }
        if (DateTime.UtcNow < _next) return;
        _next = DateTime.UtcNow.AddMilliseconds(550);
        try {
            FightWindow fight = UIManager.inst.Fight;
            if (!fight.isShowing || !(fight.contentPane is UIFightWindow pane) ||
                pane.isDisposed || pane.step == null || pane.step.selectedIndex < 2 ||
                pane.step.selectedIndex > 5) { Clear(); return; }

            BattleSceneController scene = BattleSceneController.inst;
            BattleShowDirector? director = scene?.directorManager;
            BattleActor? attacker = director?.attacker;
            BattleActor? defender = director?.victim;
            GameLogicManager logic = GameLogicManager.inst;
            BattleLogic battle = logic.battle;
            BattleRole? attackRole = logic.fight?.attackData;
            BattleRole? defendRole = logic.fight?.defendData;
            if (attacker == null || defender == null || attackRole == null || defendRole == null ||
                battle == null || attackRole.PlayerId == defendRole.PlayerId) { Clear(); return; }
            BattlePlayerData attack = battle.GetPlayerDataById(attackRole.PlayerId);
            BattlePlayerData defend = battle.GetPlayerDataById(defendRole.PlayerId);
            if (attack?.buffContainer == null || defend?.buffContainer == null ||
                attacker.effectParent == null || defender.effectParent == null) { Clear(); return; }

            if (!ReferenceEquals(_director, director) || !ReferenceEquals(_fightWindow, fight) ||
                !ReferenceEquals(_attacker, attacker) || !ReferenceEquals(_defender, defender)) {
                Clear();
                _director = director;
                _fightWindow = fight;
                _attacker = attacker;
                _defender = defender;
            }
            _seen.Clear();
            SyncSide("attack:", attacker, attack);
            SyncSide("defend:", defender, defend);
            var obsolete = new List<string>();
            foreach (string key in _effects.Keys) if (!_seen.Contains(key)) obsolete.Add(key);
            foreach (string key in obsolete) Remove(key);
        } catch (Exception ex) {
            Warn(ex);
            Clear();
        }
    }

    private void SyncSide(string side, BattleActor actor, BattlePlayerData data) {
        foreach (KeyValuePair<long, Buff> pair in data.buffContainer._buffDict) {
            if (pair.Value == null) continue;
            BuffInfoConfigure config = pair.Value.BuffId.GetBuffConfigure();
            // Prevent effects already drawn by the native PK timeline from duplicating.
            if (config == null || config.EffectID <= 0 || config.IsShowEffectDuringPK) continue;
            string key = side + pair.Key;
            _seen.Add(key);
            if (_effects.TryGetValue(key, out EffectHandle existing)) {
                if (existing.EffectId == config.EffectID && ReferenceEquals(existing.Owner, actor)) continue;
                Remove(key);
            }
            if (_effects.Count >= MaxCopies) break;
            var entry = new EffectHandle(actor, config.EffectID, _generation);
            _effects[key] = entry;
            PlayEffect(key, entry);
        }
    }

    private void PlayEffect(string key, EffectHandle entry) {
        try {
            // Same API, parent and scale as BattleActor.CreateBuffEffect.
            // The actual Unity ECalls execute inside the native game's manager.
            var awaiter = EffectManager.inst.PlayById(entry.EffectId, Vector3.zero,
                Quaternion.identity, entry.Owner.effectParent, null, .25f).GetAwaiter();
            if (awaiter.IsCompleted) {
                FinishEffect(key, entry, awaiter.GetResult);
            } else {
                awaiter.OnCompleted(() => FinishEffect(key, entry, awaiter.GetResult));
            }
        } catch (Exception ex) {
            Warn(ex);
            Remove(key);
        }
    }

    private void FinishEffect(string key, EffectHandle entry, Func<Effect> result) {
        Effect? effect = null;
        try {
            effect = result();
            if (effect == null) {
                if (_effects.TryGetValue(key, out EffectHandle pending) &&
                    ReferenceEquals(pending, entry)) _effects.Remove(key);
                return;
            }
            if (entry.Generation == _generation && _effects.TryGetValue(key, out EffectHandle active) &&
                ReferenceEquals(active, entry) && Enabled) {
                entry.Instance = effect;
                return;
            }
        } catch (Exception ex) {
            Warn(ex);
        }
        // The effect completed after the battle or option was switched off.
        if (effect != null) Release(effect);
        if (_effects.TryGetValue(key, out EffectHandle current) && ReferenceEquals(current, entry))
            _effects.Remove(key);
    }

    private void Remove(string key) {
        if (!_effects.TryGetValue(key, out EffectHandle entry)) return;
        _effects.Remove(key);
        if (entry.Instance != null) Release(entry.Instance);
    }

    private static void Release(Effect effect) {
        try { effect.ReleaseEffect(); } catch (Exception) {
            // ignored
        }
    }

    private void Warn(Exception ex) {
        if (DateTime.UtcNow < _warnAt) return;
        _warnAt = DateTime.UtcNow.AddSeconds(30);
        SdkLog.Warn("BattleBuffDisplay", "战斗 Buff 特效暂不可用：" + ex.Message);
    }

    internal void Clear() {
        _generation++;
        var ids = new List<string>(_effects.Keys);
        foreach (string key in ids) Remove(key);
        _seen.Clear();
        _director = null;
        _fightWindow = null;
        _attacker = null;
        _defender = null;
    }

    public void Dispose() => Clear();

    private sealed class EffectHandle {
        internal readonly BattleActor Owner;
        internal readonly int EffectId;
        internal readonly int Generation;
        internal Effect? Instance;

        internal EffectHandle(BattleActor owner, int id, int generation) {
            Owner = owner;
            EffectId = id;
            Generation = generation;
        }
    }
}