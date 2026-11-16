using BattleBuffDisplay.Features;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Logging;
using Core.Scene;
using UI;
using System;
using GameSceneManager = Core.Scene.SceneManager;
using CesiumLoader.SDK.Mods;
using UnityEngine.SceneManagement;
using Scene = UnityEngine.SceneManagement.Scene;

namespace BattleBuffDisplay;

/// <summary>独立显示战斗双方 Buff；额外 Buff 特效由配置文件决定。</summary>
public sealed class BattleBuffDisplayMod : ModBase {
    private FightBuffListService? _lists;
    private FightBuffVisualService? _effects;
    private DateTime _nextWarning = DateTime.MinValue;
    private bool _activeFight;

    public override string Name => "BattleBuffDisplay";
    public override string Version => "0.1.6";
    public override string Description => "战斗 Buff 列表与可选附加特效";

    public override void OnLoad() => Log.Info("战斗 Buff 显示已加载");

    public override void OnInitialize() {
        // The Buff list is always enabled, even if an SDK build provides no
        // configuration service. Only the optional effects depend on config.
        if (Config != null) {
            Config.SetDefault("battle.fightBuffEffects", false,
                ConfigDoc.All("显示额外 Buff 特效（实验，可能影响性能）"));
            Config.SaveIfDirty();
        } else {
            Log.Warn("配置尚未就绪，使用默认关闭的附加特效");
        }
        _lists = new FightBuffListService { Enabled = true };
        _effects = new FightBuffVisualService();
        ApplyConfiguration();
    }

    public override void OnConfigChanged() => ApplyConfiguration();

    private void ApplyConfiguration() {
        if (_effects == null || Config == null) return;
        _effects.Enabled = Config.GetBool("battle.fightBuffEffects");
        if (!_effects.Enabled) _effects.Clear();
    }

    public override void OnUpdate() {
        try {
            // The loader starts ticking before the game is actually in a
            // battle. Do not obtain UIManager.Fight here: its getter creates a
            // FightWindow and can interfere with the home-screen transition.
            if (!GameSceneManager.hasInstance ||
                GameSceneManager.inst.currentType.Value != SceneType.Battle ||
                !UIManager.hasInstance) {
                if (_activeFight) ClearScene();
                return;
            }
            FightWindow? fight = ActiveFightWindow.Find();
            _activeFight = fight != null;
            _lists?.Tick(fight);
            if (_effects?.Enabled == true) _effects.Tick(fight);
        } catch (Exception error) {
            ClearScene();
            if (DateTime.UtcNow >= _nextWarning) {
                _nextWarning = DateTime.UtcNow.AddSeconds(20);
                SdkLog.Warn("BattleBuffDisplay", "本帧战斗 Buff 绘制已安全跳过：" + error.Message);
            }
        }
    }

    public override void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        ClearScene();
    }
    
    public override void OnSceneUnloaded(Scene scene) {
        ClearScene();
    }

    private void ClearScene() {
        _activeFight = false;
        // A scene unload can invalidate FairyGUI and effect handles before
        // managed OnSceneUnloaded runs. Cleanups must not break scene loading.
        try { _lists?.Clear(); } catch (Exception) { /* already unloaded */ }
        try { _effects?.Clear(); } catch (Exception) { /* already unloaded */ }
    }

    public override void OnUnload() {
        _effects?.Dispose();
        _effects = null;
        _lists?.Dispose();
        _lists = null;
    }
}