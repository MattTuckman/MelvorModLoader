using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;

namespace OneStyle;

/// <summary>
/// How an elite looks: its own picture with a cyan (Champion) or orange (Unique) outline, its name over the monster and
/// a short banner when a Unique appears. The game redraws the monster and its name whenever the fight changes state,
/// so this is put on again after each of those (on the next frame, after the game's own update).
/// </summary>
public static partial class Elites
{
    public static string Dir = "";
    public static readonly Color ChampionColor = new(0.22f, 0.91f, 1f);    // #38E8FF
    public static readonly Color UniqueColor = new(1f, 0.55f, 0.14f);      // #FF8C24
    static readonly Dictionary<string, Texture2D?> s_textures = new();
    static bool s_lookOn, s_bannerDue, s_spriteDirty;
    static CanvasLayer? s_bannerLayer;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateSpriteScale")]
    static extern void UpdateSpriteScale(EnemySprite sprite);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetSpriteTexture")]
    static extern void SetSpriteTexture(EnemySprite sprite, Enemy enemy);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label NameLabelOf(EnemyNamePanel panel);

    static void OnStateChanged(object? sender, ValueChangedEventArgs<CombatActionState> e) => ShowLook();

    /// <summary>Puts the look on (or takes it off) on the next frame; does nothing off the main thread.</summary>
    static void ShowLook()
    {
        if (System.Environment.CurrentManagedThreadId != s_mainThread) return;
        if (Tier == EliteTier.Unique && !s_lookOn) s_bannerDue = true;
        Callable.From(ApplyLook).CallDeferred();
    }

    public static Texture2D? TextureOf(EliteInfo info, EliteTier tier)
    {
        string path = Path.Combine(Dir, "art", tier == EliteTier.Unique ? "unique" : "champion", info.File + ".webp");
        if (s_textures.TryGetValue(path, out Texture2D? cached)) return cached;
        Texture2D? texture = null;
        try
        {
            if (File.Exists(path))
            {
                Image image = Image.LoadFromFile(path);
                texture = ImageTexture.CreateFromImage(image);
            }
        }
        catch (Exception e) { Log("One Style: an elite picture could not be loaded: " + e.Message); }
        s_textures[path] = texture;
        return texture;
    }

    static IEnumerable<T> Find<T>(Node node) where T : Node
    {
        if (node is T t) yield return t;
        foreach (Node child in node.GetChildren())
            foreach (T x in Find<T>(child)) yield return x;
    }

    static void ApplyLook()
    {
        try
        {
            if (s_combat == null || Engine.GetMainLoop() is not SceneTree tree) return;
            bool on = Tier != EliteTier.Normal && Info != null && Enemy != null && ReferenceEquals(s_combat.Enemy.Enemy, Enemy)
                      && s_combat.Action.State is CombatActionState.Spawning or CombatActionState.Fighting;
            if (!on && !s_lookOn && !s_spriteDirty) return;
            Color color = Tier == EliteTier.Unique ? UniqueColor : ChampionColor;
            Texture2D? texture = on ? TextureOf(Info!, Tier) : null;
            bool restored = false;
            foreach (EnemySprite view in Find<EnemySprite>(tree.Root))
            {
                if (on && texture != null)
                {
                    Vector2 size = texture.GetSize();
                    float scale = Enemy!.Sprite.GetHeight() > 0 ? size.Y / Enemy.Sprite.GetHeight() : 1;
                    view.Sprite.Texture = texture;
                    view.OutlineSprite.Texture = texture;
                    view.Sprite.Offset = new Vector2(-size.X / 2 + Enemy.SpriteOffset.X * scale, -size.Y + Enemy.SpriteOffset.Y * scale);
                    view.OutlineSprite.Offset = view.Sprite.Offset;
                    view.OutlineSprite.Modulate = color;
                    UpdateSpriteScale(view);
                    s_spriteDirty = true;
                }
                else
                {
                    // the game only redraws when the monster changes: the same monster as a normal one gets its own
                    // picture back through the game's own method
                    view.OutlineSprite.Modulate = Colors.White;
                    if (s_spriteDirty && s_combat.Enemy.Enemy is { } normal)
                    {
                        // The game sets the picture only when it differs from the one it set last: empty it first.
                        view.DefaultTexture = null;
                        SetSpriteTexture(view, normal);
                        restored = true;
                    }
                }
            }
            foreach (EnemyNamePanel panel in Find<EnemyNamePanel>(tree.Root))
            {
                Label label = NameLabelOf(panel);
                if (on && s_combat.Action.State == CombatActionState.Fighting)
                {
                    string monster = Enemy!.DisplayName.Text;
                    label.Text = Tier == EliteTier.Unique ? Info!.UniqueName : "Champion " + monster;
                    label.Modulate = color;
                    string affixes = Affixes.Count > 0 ? string.Join(" · ", Affixes) + (s_enraged ? " (enraged)" : "") : "";
                    panel.TooltipText = Tier == EliteTier.Unique
                        ? $"Unique {monster}\n{Info!.Story}\n{affixes}{(SignatureOn ? "\nSignature: " + Info.Signature.Replace("MelvorBase:", "") : "")}"
                        : $"Champion {monster}\n{affixes}";
                }
                else if (!on)
                {
                    panel.Update();
                    label.Modulate = Colors.White;
                    panel.TooltipText = "";
                }
            }
            UpdateBadges(tree, on && s_combat.Action.State == CombatActionState.Fighting);
            if (on && s_bannerDue && Tier == EliteTier.Unique) Banner(tree, $"{Info!.UniqueName}", $"Unique {Enemy!.DisplayName.Text}");
            s_bannerDue = false;
            s_lookOn = on;
            if (restored) s_spriteDirty = false;
        }
        catch (Exception e) { Log("One Style: the elite look failed: " + e.Message); }
    }

    /// <summary>A short banner at the top of the screen when a Unique appears.</summary>
    static void Banner(SceneTree tree, string title, string under)
    {
        if (s_bannerLayer == null || !GodotObject.IsInstanceValid(s_bannerLayer))
        {
            s_bannerLayer = new CanvasLayer { Layer = 90 };
            tree.Root.AddChild(s_bannerLayer);
        }
        foreach (Node n in s_bannerLayer.GetChildren()) n.QueueFree();
        // the full width of the screen, the text centred, below the top bar
        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        box.OffsetTop = 64;
        var big = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center };
        big.AddThemeFontSizeOverride("font_size", 30);
        big.AddThemeColorOverride("font_color", UniqueColor);
        big.AddThemeConstantOverride("outline_size", 6);
        big.AddThemeColorOverride("font_outline_color", Colors.Black);
        var small = new Label { Text = under, HorizontalAlignment = HorizontalAlignment.Center };
        small.AddThemeFontSizeOverride("font_size", 16);
        small.AddThemeConstantOverride("outline_size", 4);
        small.AddThemeColorOverride("font_outline_color", Colors.Black);
        box.AddChild(big);
        box.AddChild(small);
        s_bannerLayer.AddChild(box);
        tree.CreateTimer(4.0).Timeout += () => { if (GodotObject.IsInstanceValid(box)) box.QueueFree(); };
    }

    /// <summary>For tests: the monster view shows the game's own picture of the monster.</summary>
    public static bool ShowsGamePicture()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || s_combat?.Enemy.Enemy is not { } enemy) return false;
        EnemySprite? view = Find<EnemySprite>(tree.Root).FirstOrDefault();
        return view != null && ReferenceEquals(view.Sprite.Texture, enemy.Sprite);
    }

    /// <summary>For tests: what the monster view shows now.</summary>
    public static (int TextureHeight, string Name, Color Outline) Shown()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return (0, "", Colors.White);
        EnemySprite? view = Find<EnemySprite>(tree.Root).FirstOrDefault();
        EnemyNamePanel? panel = Find<EnemyNamePanel>(tree.Root).FirstOrDefault();
        return (view?.Sprite.Texture?.GetHeight() ?? 0, panel != null ? NameLabelOf(panel).Text : "", view?.OutlineSprite.Modulate ?? Colors.White);
    }
}
