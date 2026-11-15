using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;

namespace BattleBuffDisplay;

/// <summary>CesiumLoader 独立程序集入口。</summary>
public static class ModEntry {
    public static void Main() {
        SdkManifest.ExportSidecar();
        ModBase.Run(new BattleBuffDisplayMod(), tag: "BattleBuffDisplay");
    }
}