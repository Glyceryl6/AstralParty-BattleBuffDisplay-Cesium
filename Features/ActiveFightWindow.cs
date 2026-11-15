using UI;

namespace BattleBuffDisplay.Features;

/// <summary>
/// Look up an already SHOWN fight window only. Accessing UIManager.Fight
/// creates and caches a new FightWindow even when the user is in the home
/// scene; that must not happen from a background MOD's update loop.
/// </summary>
internal static class ActiveFightWindow {
    internal static FightWindow? Find() {
        if (!UIManager.hasInstance) return null;
        foreach (BaseWindow window in UIManager.inst.propUpWindows) {
            if (window is FightWindow fight && fight.isShowing &&
                fight.contentPane is UIFightWindow pane &&
                !pane.isDisposed && pane.onStage && pane.step != null)
                return fight;
        }
        return null;
    }
}
