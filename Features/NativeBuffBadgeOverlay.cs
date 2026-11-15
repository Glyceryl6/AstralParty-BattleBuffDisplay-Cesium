using System;
using FairyGUI;
using UI;

namespace BattleBuffDisplay.Features;

/// <summary>
/// Reuses the original Common_Internal/Button_Buff badges, without the
/// native-pointer cloning performed by the old IL2CPP plugin. Only labels
/// are retained; the existing UICom_BuffInfoItem already draws the icon.
/// </summary>
internal static class NativeBuffBadgeOverlay {
    private const string Name = "BattleBuffDisplay.BuffNativeCounters";
    private static bool _registered;

    internal static void Apply(UICom_BuffInfoItem? row, int progress, int rounds) {
        if (row == null || row.isDisposed || row.loader_Buff == null) return;
        UIButton_Buff? badge = row.GetChild(Name) as UIButton_Buff;
        if (progress <= 0 && rounds <= 0) {
            if (badge != null) badge.visible = false;
            return;
        }
        if (badge == null) {
            if (!_registered) {
                UIObjectFactory.SetPackageItemExtension(UIButton_Buff.URL, typeof(UIButton_Buff));
                _registered = true;
            }
            badge = UIButton_Buff.CreateInstance();
            if (badge == null) return;
            badge.name = Name;
            badge.touchable = false;
            badge.opaque = false;
            for (int i = 0; i < badge.numChildren; i++) {
                GObject child = badge.GetChildAt(i);
                child.visible = ReferenceEquals(child, badge.txt_Num) || ReferenceEquals(child, badge.txt_Round);
            }
            row.AddChild(badge);
        }
        if (badge.loader_Icon != null && badge.loader_Icon.width > 0f && badge.loader_Icon.height > 0f) {
            GLoader icon = row.loader_Buff;
            float scale = Math.Min(icon.width / badge.loader_Icon.width, icon.height / badge.loader_Icon.height);
            scale = Math.Max(.35f, Math.Min(2.5f, scale));
            badge.SetScale(scale, scale);
            badge.SetXY(icon.x - badge.loader_Icon.x * scale, icon.y - badge.loader_Icon.y * scale);
        }
        badge.visible = true;
        if (badge.txt_Num != null) {
            badge.txt_Num.visible = progress > 0;
            badge.txt_Num.text = progress.ToString();
        }
        if (badge.txt_Round != null) {
            badge.txt_Round.visible = rounds > 0;
            badge.txt_Round.text = rounds.ToString();
        }
    }
}