using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;

namespace PhoneScreen;

/// <summary>
/// Remembered between games: the network and port chosen. With "Reach from anywhere" also the relay's secret and
/// key, the paired phones (hashes of their tokens, so they need not pair again) and, only if asked for, the
/// controls switch.
/// </summary>
public sealed class PhoneSettings
{
    public string Address { get; set; } = "";
    public int Port { get; set; } = 48731;
    public bool Remote { get; set; }
    /// <summary>The relay ("" = the one this mod comes with).</summary>
    public string RelayUrl { get; set; } = "";
    public string RelaySecret { get; set; } = "";
    public string RelayKey { get; set; } = "";
    public List<string> Phones { get; set; } = new();
    /// <summary>On as soon as the game starts, phones paired before still paired (for use away from the PC).</summary>
    public bool AutoStart { get; set; }
    public bool KeepControls { get; set; }
    public bool ControlsOn { get; set; }
}

/// <summary>
/// Phone Screen: a small web page served by the game on your home network, for a phone's browser. Its key (F2 unless changed in the
/// loader's Mods menu) or the Phone button in the top bar opens the window with the switch and the QR code. Off until you start it.
/// </summary>
public class PhoneMod : IMod
{
    public static PhoneMod? Instance { get; private set; }

    ModContext _ctx = null!;
    public Server Server { get; } = new();
    public RelayLink Relay { get; private set; } = null!;

    /// <summary>The relay this mod comes with (a Cloudflare Worker: Relay\worker.js). Empty until it is online.</summary>
    public const string DefaultRelay = "";

    /// <summary>The relay to use: the test's (MML_RELAY), the settings', or the default.</summary>
    public string RelayUrl => System.Environment.GetEnvironmentVariable("MML_RELAY") is { Length: > 0 } test ? test
                              : Settings.RelayUrl != "" ? Settings.RelayUrl : DefaultRelay;
    public Snapshot Snapshot { get; } = new();
    public Catalog Catalog { get; } = new();
    bool _catalogDirty = true;
    public PhoneWindow? Window { get; private set; }
    public PhoneSettings Settings { get; private set; } = new();
    WindowKey? _key;
    ulong _lastSnap, _lastWindow;
    Button? _button;
    int _frame;

    string SettingsFile => Path.Combine(_ctx.Directory, "phone.json");

    public void OnLoad(ModContext ctx)
    {
        Instance = this;
        _ctx = ctx;
        Server.Log = ctx.Log;
        Server.Page = Page.Html;
        Relay = new RelayLink(Server, ctx.Log);
        try { if (File.Exists(SettingsFile)) Settings = JsonSerializer.Deserialize<PhoneSettings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { Settings = new(); }
        Server.SessionsChanged += () => { if (Remembers) { Settings.Phones = Server.SessionHashes(); SaveSettings(); } };
        Window = new PhoneWindow(this, ctx.Tree);
        _key = new WindowKey(ctx, "Phone Screen", () => Window?.Toggle(), Key.F2);
        _key.Changed += () => { if (_button != null && GodotObject.IsInstanceValid(_button)) _button.TooltipText = ButtonTip; };
        ctx.Frame += Tick;
        // on by itself: the player may be away from the PC, with nobody to press Start
        if (Settings.AutoStart || (Settings.Remote && RelayUrl != ""))
        {
            var addresses = PhoneWindow.Addresses();
            IPAddress address = IPAddress.TryParse(Settings.Address, out IPAddress? a) && addresses.Any(x => x.Ip.Equals(a)) ? a : addresses[0].Ip;
            if (StartServer(address)) ctx.Log($"The phone screen is on ({address}).");
        }
        ctx.Log($"Ready. Press {_key.Spoken} (or the Phone button) to start the phone screen.");
    }

    public void SaveSettings()
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { _ctx.Log("The settings could not be saved: " + e.Message); }
    }

    /// <summary>Starts serving on the address (the port remembered, or the next free one of a few).</summary>
    public bool StartServer(IPAddress address, int? port = null)
    {
        int first = port ?? Settings.Port;
        for (int p = first; p < first + 6; p++)
        {
            if (Server.Start(address, p))
            {
                Settings.Address = address.ToString();
                Settings.Port = port == null ? first : Settings.Port;
                SaveSettings();
                _lastSnap = 0;
                if (Remembers)
                {
                    Server.Restore(Settings.Phones);
                    if (Settings.KeepControls) Server.ControlsAllowed = Settings.ControlsOn;
                }
                if (Settings.Remote) StartRelay();
                return true;
            }
            if (!Server.Error.Contains("in use")) break;
        }
        return false;
    }

    public void StopServer()
    {
        Relay.Stop();
        Server.Stop();
        _ctx.Log("The phone screen is off.");
    }

    /// <summary>Turns "Reach from anywhere" on or off (a secret and a key are made the first time).</summary>
    public void SetRemote(bool on)
    {
        Settings.Remote = on;
        if (on)
        {
            if (Settings.RelaySecret == "" || RelayLink.FromB64u(Settings.RelayKey).Length != 32) NewRelayKeys();
            Settings.Phones = Server.SessionHashes();
            if (Server.Running) StartRelay();
        }
        else
        {
            Relay.Stop();
            if (!Remembers) { Settings.Phones.Clear(); Settings.ControlsOn = false; }
        }
        SaveSettings();
    }

    /// <summary>Phones (and, if asked, the controls switch) are remembered while the phone screen starts by itself.</summary>
    public bool Remembers => Settings.AutoStart || Settings.Remote;

    /// <summary>"Turn on by itself when the game starts": the phones paired now stay paired.</summary>
    public void SetAutoStart(bool on)
    {
        Settings.AutoStart = on;
        if (Remembers) Settings.Phones = Server.SessionHashes();
        else { Settings.Phones.Clear(); Settings.ControlsOn = false; Settings.KeepControls = false; }
        SaveSettings();
    }

    /// <summary>The controls switch; remembered only when the player asked for that.</summary>
    public void SetControls(bool on)
    {
        Server.ControlsAllowed = on;
        Settings.ControlsOn = on && Settings.KeepControls;
        SaveSettings();
    }

    /// <summary>Every phone out: their sessions, and with the relay a new room and key (the old QR code stops working).</summary>
    public void SignOutAll()
    {
        Server.RevokeAll();
        Settings.Phones.Clear();
        if (Settings.Remote)
        {
            NewRelayKeys();
            if (Server.Running) StartRelay();
        }
        SaveSettings();
    }

    void NewRelayKeys()
    {
        Settings.RelaySecret = RelayLink.NewSecret();
        Settings.RelayKey = RelayLink.NewSecret();
    }

    void StartRelay()
    {
        if (RelayUrl == "") return;
        try { Relay.Start(RelayUrl, Settings.RelaySecret, Settings.RelayKey); }
        catch (Exception e) { _ctx.Log("The relay could not start: " + e.Message); }
    }

    /// <summary>The QR code's link: through the relay when reached from anywhere, else the home network's.</summary>
    public string PairingLink => Settings.Remote && Relay.Running ? Relay.Link(Server.Code) : Server.PairingLink;

    void Tick()
    {
        _key?.Poll();
        ulong now = Time.GetTicksMsec();
        if (Window is { Visible: true } && now - _lastWindow > 500) { _lastWindow = now; Window.Tick(); }
        if (++_frame % 60 == 0) PlaceButton();
        if (!Server.Running) return;
        // the phone's numbers read "5,000" like the game's own, whatever the PC's language (a Turkish PC wrote "5.000"
        // in the texts made here and in the other mods, beside "5,000" from the page)
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { TickServer(now); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    void TickServer(ulong now)
    {
        Game? game = GameAccess.CurrentGame;
        // commands first, then a fresh snapshot (so a phone sees the answer at once)
        while (Server.Commands.TryDequeue(out PhoneCommand? c)) Carry(c, game);
        if (game is { IsSaveLoaded: true }) Serve(game, now);
        if (now - _lastSnap < 500) return;
        _lastSnap = now;
        try { Server.Publish(game is { IsSaveLoaded: true } ? Snapshot.Take(game) : Snapshot.NotReady("No character is open in the game.")); }
        catch (Exception e) { _ctx.Log("The phone snapshot failed: " + e.Message); }
    }

    ulong _lastCatalog;

    /// <summary>
    /// What the phones asked for besides the snapshot, a little each frame: pictures (a few per frame), the catalogue
    /// (again every few seconds while a phone looks at it, at once after a command changed something), and the Combat
    /// Advisor's verdicts for an area.
    /// </summary>
    void Serve(Game game, ulong now)
    {
        for (int i = 0; i < 6 && Server.IconsWanted.TryDequeue(out string? key); i++)
            Server.PublishIcon(key, Catalog.Picture(game, key));
        bool wanted = DateTime.UtcNow - Server.CatalogWanted < TimeSpan.FromSeconds(20);
        if (wanted && (now - _lastCatalog > 6000 || (_catalogDirty && now - _lastCatalog > 400)))
        {
            _lastCatalog = now;
            _catalogDirty = false;
            try { Server.PublishCatalog(Catalog.Build(game)); }
            catch (Exception e) { _ctx.Log("The phone catalogue failed: " + e.Message); }
        }
        // one of the other screens a phone looks at, made again when older than a second and a half (one per frame)
        foreach (var (name, asked) in Server.PartsWanted)
        {
            if (DateTime.UtcNow - asked > TimeSpan.FromSeconds(6)) continue;
            if (DateTime.UtcNow - Server.PartMade(name) < TimeSpan.FromMilliseconds(1500)) continue;
            try { Server.PublishPart(name, Parts.Build(game, name)); }
            catch (Exception e)
            {
                _ctx.Log($"The phone's {name} screen failed: " + e.Message);
                Server.PublishPart(name, System.Text.Encoding.UTF8.GetBytes("{\"part\":\"" + name + "\",\"available\":false}"));
            }
            break;
        }
        try
        {
            if (Catalog.Advise(game, () => Server.AdviceWanted.TryDequeue(out string? area) ? area : null) is { } advice)
                Server.PublishAdvice(advice.Area, advice.Json);
        }
        catch (Exception e) { _ctx.Log("The phone's advisor request failed: " + e.Message); }
    }

    /// <summary>A phone's command, on the game's own thread, checked again here.</summary>
    void Carry(PhoneCommand c, Game? game)
    {
        if (Snapshot.Answered(c.Id)) return;                                    // (the same command twice)
        if (DateTime.UtcNow - c.Received > TimeSpan.FromSeconds(10)) { Snapshot.Answer(c.Id, false, "Too late: the command expired."); return; }
        if (!Server.ControlsAllowed) { Snapshot.Answer(c.Id, false, "Phone controls are off."); return; }
        if (game is not { IsSaveLoaded: true }) { Snapshot.Answer(c.Id, false, "No character is open in the game."); return; }
        if (c.Character != game.CharacterName) { Snapshot.Answer(c.Id, false, "That was for another character."); return; }
        try
        {
            switch (c.Cmd)
            {
                case "eat":
                {
                    if (!game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? e) || e is not CombatEntity combat || !combat.Action.IsStarted)
                    { Snapshot.Answer(c.Id, false, "You are not fighting."); return; }
                    var food = combat.Food.SelectedFood?.Item;
                    if (food == null || combat.Food.Inventory.GetQuantity(food) <= 0) { Snapshot.Answer(c.Id, false, "No food selected, or none left."); return; }
                    int before = combat.Player.Health.Current;
                    combat.Food.ManualEat();
                    Snapshot.Answer(c.Id, true, $"Ate 1 {food.DisplayName.GetPluralText(1)}: hitpoints {before} -> {combat.Player.Health.Current}.");
                    Snapshot.Alert($"Ate 1 {food.DisplayName.GetPluralText(1)} (from the phone).");
                    return;
                }
                case "pausequeue":
                case "resumequeue":
                {
                    var (ok, text) = Snapshot.QueueCommand(c.Cmd == "pausequeue");
                    Snapshot.Answer(c.Id, ok, text);
                    if (ok) Snapshot.Alert(c.Cmd == "pausequeue" ? "Queue paused from the phone." : "Queue resumed from the phone.");
                    return;
                }
            }
            (bool Ok, string Text)? done = c.Cmd switch
            {
                "start" => Catalog.Start(game, c.Arg("skill"), c.Arg("target"), c.Arg("extra")),
                "stop" => Catalog.Stop(game, c.Arg("skill")),
                "stopall" => Catalog.StopAll(game),
                "food" => Catalog.Food(game, c.Arg("item")),
                "stance" => Catalog.Stance(game, c.Arg("stance")),
                "sell" => Catalog.Sell(game, c.Arg("item"), c.Number("qty")),
                "qadd" => Catalog.QueueAdd(game, c.Arg("skill"), c.Arg("target"), c.Arg("extra"), c.Arg("until"), c.Number("amount")),
                "qremove" or "qmove" or "qclear" or "qload" or "qsave" => Catalog.QueueEdit(c.Cmd, (int)c.Number("index"), (int)c.Number("by"), c.Arg("name")),
                "equip" => Parts.Equip(game, c.Arg("item"), c.Arg("slot"), c.Number("qty")),
                "unequip" => Parts.Unequip(game, c.Arg("slot")),
                "loadout" => Parts.Loadout(game, c.Arg("loadout")),
                "potion" => Parts.Potion(game, c.Arg("entity"), c.Arg("slot"), c.Arg("item")),
                "prayer" => Parts.Prayer(game, c.Arg("prayer")),
                "spell" => Parts.Spell(game, c.Arg("spell"), false),
                "imbue" => Parts.Spell(game, c.Arg("spell"), true),
                "slayer" => Parts.SlayerDo(game, c.Arg("master"), c.Arg("op")),
                "plant" => Parts.Plant(game, c.Arg("plot"), c.Arg("seed"), c.Arg("compost")),
                "harvest" => Parts.Harvest(game, c.Arg("plot")),
                "buyplot" => Parts.BuyPlot(game, c.Arg("plot")),
                "buy" => Parts.Buy(game, c.Arg("purchase"), c.Number("qty")),
                "adv" => Parts.Adventurer(game, c.Arg("who"), c.Arg("op"), c.Arg("a"), c.Arg("b")),
                "bulk" => Parts.Bulk(game, c.Arg("op")),
                "slots" => Parts.Slots((int)c.Number("extra")),
                "slotdrop" => Parts.SlotDrop((int)c.Number("index")),
                "slotwait" => Parts.SlotWait(c.Arg("on") == "1"),
                "cheat" => double.TryParse(c.Arg("value"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cv)
                    ? Parts.Cheat(game, c.Arg("cheat"), cv) : (false, "Pick a value."),
                _ => null,
            };
            if (done is { } d)
            {
                Snapshot.Answer(c.Id, d.Ok, d.Text);
                if (d.Ok) { Snapshot.Alert(d.Text + " (from the phone)"); _catalogDirty = true; }
                Server.PartsChanged();
                return;
            }
            Snapshot.Answer(c.Id, false, "Unknown command.");
        }
        catch (Exception e) { Snapshot.Answer(c.Id, false, "It did not work: " + (e.InnerException ?? e).Message); }
        finally { _lastSnap = 0; }
    }

    string ButtonTip => (_key?.With("Phone Screen") ?? "Phone Screen") + "\nFollow your character from your phone's browser on the same Wi-Fi.";

    /// <summary>A small "Phone" button at the end of the game's top bar (its key does the same).</summary>
    void PlaceButton()
    {
        try
        {
            if (_button != null && GodotObject.IsInstanceValid(_button) && _button.IsInsideTree()) return;
            _button = null;
            if (_ctx.Tree.Root.FindChild("GuideNavigationButton", true, false) is not Control guide || guide.GetParent() is not BoxContainer row) return;
            if (row.FindChild("PhoneNavigationButton", false, false) is Button old) { row.RemoveChild(old); old.QueueFree(); }
            LabelSettings? letters = (guide.FindChild("Title", true, false) as Label)?.LabelSettings;
            var button = new Button
            {
                Name = "PhoneNavigationButton", Flat = true, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(64, 36),
                SizeFlagsVertical = guide.SizeFlagsVertical, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                TooltipText = ButtonTip,
            };
            var hover = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.07f) };
            hover.SetCornerRadiusAll(4);
            button.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("hover", hover);
            button.AddThemeStyleboxOverride("pressed", hover);
            var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
            column.AddThemeConstantOverride("separation", 0);
            column.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            // a little phone, drawn (no picture of a phone in the game)
            var icon = new Control { CustomMinimumSize = new Vector2(24, 24), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
            icon.Draw += () =>
            {
                var gold = new Color(0.93f, 0.75f, 0.35f);
                icon.DrawRect(new Rect2(7, 1, 11, 21), new Color(0.15f, 0.13f, 0.11f));
                icon.DrawRect(new Rect2(7, 1, 11, 21), gold, false, 2);
                icon.DrawRect(new Rect2(9, 4, 7, 12), new Color(0.35f, 0.62f, 0.78f));
                icon.DrawCircle(new Vector2(12.5f, 19), 1.3f, gold);
            };
            column.AddChild(icon);
            var title = new Label { Text = "Phone", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            if (letters != null) title.LabelSettings = letters;
            column.AddChild(title);
            button.AddChild(column);
            button.Pressed += () => Window?.Toggle();
            row.AddChild(button);
            row.MoveChild(button, guide.GetIndex() + 1);
            _button = button;
        }
        catch { }
    }
}
