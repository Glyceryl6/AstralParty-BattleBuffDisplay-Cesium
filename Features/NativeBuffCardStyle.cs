using System;
using FairyGUI;
using UI;
using UnityEngine;

namespace BattleBuffDisplay.Features;

/// <summary>
/// Restores the dark rounded card and warm outline used by AstralProbe's
/// original Buff list, but delegates corner counters to UIButton_Buff.
/// No native pointer, custom material or cloned Unity object is retained.
/// </summary>
internal static class NativeBuffCardStyle {
    private const string BackgroundName = "BattleBuffDisplay.BuffCardBackground";
    private static readonly Color Border = new Color(.95f, .79f, .49f, 1f);
    private static readonly Color Fill = new Color(.10f, .14f, .22f, .82f);

    internal static void Style(UICom_BuffInfoItem item, int progress, int rounds) {
        if (item == null || item.isDisposed) return;
        GGraph? background = item.GetChild(BackgroundName) as GGraph;
        if (background == null) {
            background = new GGraph { name = BackgroundName };
            background.touchable = false;
            item.AddChild(background);
            item.SetChildIndex(background, 0);
        }
        // Keep the entire round outline inside the row's rendered bounds.
        float width = Math.Max(1f, item.width - 6f);
        float height = Math.Max(1f, item.height - 6f);
        background.SetXY(3f, 3f);
        background.SetSize(width, height);
        // Matches the former 2px golden outline / 10px corner radius.
        // The Shape API is the official FairyGUI Unity API.
        background.shape.DrawRoundRect(2f, Border, Fill, 10f, 10f, 10f, 10f);
        NativeBuffBadgeOverlay.Apply(item, progress, rounds);
    }

    internal static void RefreshBorder(UICom_BuffInfoItem item) {
        if (item == null || item.isDisposed) return;
        GGraph? background = item.GetChild(BackgroundName) as GGraph;
        if (background == null) return;
        background.SetXY(3f, 3f);
        background.SetSize(Math.Max(1f, item.width - 6f),
            Math.Max(1f, item.height - 6f));
        background.shape.DrawRoundRect(2f, Border, Fill, 10f, 10f, 10f, 10f);
    }
}