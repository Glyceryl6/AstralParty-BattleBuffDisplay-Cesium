using BattleBuffDisplay.Features;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using UnityEngine.SceneManagement;

namespace BattleBuffDisplay;

/// <summary>独立显示战斗双方 Buff；额外 Buff 特效由配置文件决定。</summary>
public sealed class BattleBuffDisplayMod : ModBase {
    private FightBuffListService? _lists;
    private FightBuffVisualService? _effects;

    public override string Name => "BattleBuffDisplay";
    public override string Version => "0.1.0";
    public override string Description => "战斗 Buff 列表与可选附加特效";

    public override void OnLoad() => Log.Info("战斗 Buff 显示已加载");

    public override void OnInitialize() {
        if (Config == null) {
            Log.Warn("配置尚未就绪，未启动战斗 Buff 显示");
            return;
        }
        // Buff lists are always on while installed. Only optional effects
        // have a configuration flag; this MOD has no redundant master switch.
        Config.SetDefault("battle.fightBuffEffects", false,
            ConfigDoc.All("显示额外 Buff 特效（实验，可能影响性能）"));
        Config.SaveIfDirty();
        _lists = new FightBuffListService { Enabled = true };
        _effects = new FightBuffVisualService();
        ApplyConfiguration();
    }

    public override void OnConfigChanged() => ApplyConfiguration();

    private void ApplyConfiguration() {
        if (_effects == null || Config == null) return;
        _effects.Enabled = Config.GetBool("battle.fightBuffEffects", false);
        if (!_effects.Enabled) _effects.Clear();
    }

    public override void OnUpdate() {
        _lists?.Tick();
        if (_effects?.Enabled == true) _effects.Tick();
    }

    public override void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ClearScene();
    public override void OnSceneUnloaded(Scene scene) => ClearScene();

    private void ClearScene() {
        _lists?.Clear();
        _effects?.Clear();
    }

    public override void OnUnload() {
        _effects?.Dispose();
        _effects = null;
        _lists?.Dispose();
        _lists = null;
    }
}