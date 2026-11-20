using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Adventurers;
using Godot;

namespace PhoneScreen;

/// <summary>
/// The window on the PC (its key, F2 unless changed, or the Phone button): turn the phone screen on and off, pick the network, scan the
/// QR code, allow the two phone controls, sign phones out. Built in the game's own panels.
/// </summary>
public sealed class PhoneWindow
{
    readonly CanvasLayer _layer;
    readonly VBoxContainer _body;
    readonly PhoneMod _mod;
    public Control Panel { get; }
    public bool Visible => _layer.Visible;
    TextureRect? _qr;
    Label? _people, _link, _relay;
    string _qrFor = "";

    public PhoneWindow(PhoneMod mod, SceneTree tree)
    {
        _mod = mod;
        _layer = new CanvasLayer { Layer = 91, Visible = false };
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dim.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Hide(); };
        _layer.AddChild(dim);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(760, 0), MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All };
        panel.AddThemeStyleboxOverride("panel", Look.Board);
        panel.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        panel.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { panel.AcceptEvent(); Hide(); }
        };
        center.AddChild(panel);
        Panel = panel;
        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 12);
        panel.AddChild(_body);
        tree.Root.CallDeferred(Node.MethodName.AddChild, _layer);
    }

    public void Toggle() { if (Visible) Hide(); else Show(); }

    public void Show()
    {
        _layer.Visible = true;
        Build();
        Callable.From(() => { if (Visible) Panel.GrabFocus(); }).CallDeferred();
    }

    public void Hide() => _layer.Visible = false;

    /// <summary>Every half second while open: the phones count and the QR code (a new code after each pairing).</summary>
    public void Tick()
    {
        if (!Visible) return;
        Server s = _mod.Server;
        if (_people != null && GodotObject.IsInstanceValid(_people))
            _people.Text = s.Running ? $"Phones connected now: {s.Connected}   (signed in: {s.Sessions})" : "";
        if (_relay != null && GodotObject.IsInstanceValid(_relay)) _relay.Text = RelayText();
        if (s.Running && _mod.PairingLink != _qrFor) Build();
    }

    public List<string> Texts()
    {
        var found = new List<string>();
        void Walk(Node n)
        {
            if (n is CanvasItem { Visible: false }) return;
            if (n is Label l && l.Text != "") found.Add(l.Text);
            if (n is Button b && b.Text != "") found.Add(b.Text);
            foreach (Node c in n.GetChildren()) Walk(c);
        }
        Walk(_body);
        return found;
    }

    public void Build()
    {
        foreach (Node n in _body.GetChildren()) { _body.RemoveChild(n); n.QueueFree(); }
        Server s = _mod.Server;

        var head = new HBoxContainer();
        var title = Look.Heading("Phone Screen");
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(title);
        Button close = Look.Push("Close");
        close.Pressed += Hide;
        head.AddChild(close);
        _body.AddChild(head);
        _body.AddChild(Wrap("Play from your phone's browser: see what runs, start skills and fights, change gear, potions and prayers, Slayer, Farming, "
                            + "the bank and the shop, the Action Queue. The game keeps running on this PC."));

        // the network
        var net = new HBoxContainer();
        net.AddThemeConstantOverride("separation", 10);
        net.AddChild(Look.Body("Network:", 14));
        var pick = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        List<(IPAddress Ip, string Name)> choices = Addresses();
        int chosen = 0;
        for (int i = 0; i < choices.Count; i++)
        {
            pick.AddItem($"{choices[i].Ip}   ({choices[i].Name})", i);
            if (choices[i].Ip.ToString() == _mod.Settings.Address) chosen = i;
        }
        if (choices.Count > 0) pick.Select(chosen);
        pick.Disabled = s.Running;
        pick.ItemSelected += i => { _mod.Settings.Address = choices[(int)i].Ip.ToString(); _mod.SaveSettings(); Build(); };   // (the help below follows the pick)
        if (choices.Count > 0 && _mod.Settings.Address == "") _mod.Settings.Address = choices[chosen].Ip.ToString();
        net.AddChild(pick);
        Button go = Look.Push(s.Running ? "Stop" : "Start");
        go.Pressed += () =>
        {
            if (s.Running) _mod.StopServer();
            else if (choices.Count > 0) _mod.StartServer(choices[pick.Selected].Ip);
            Build();
        };
        net.AddChild(go);
        _body.AddChild(net);

        if (!s.Running)
        {
            _body.AddChild(Wrap(s.Error != "" ? "Could not start: " + s.Error : "The phone screen is off. Press Start, then scan the QR code with your phone.",
                                s.Error != "" ? new Color(1f, 0.55f, 0.4f) : Look.Grey));
        }
        else
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 18);
            bool remote = _mod.Settings.Remote && _mod.Relay.Running;
            _qr = new TextureRect { Texture = QrTexture(_mod.PairingLink), CustomMinimumSize = new Vector2(220, 220), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize };
            _qrFor = _mod.PairingLink;
            row.AddChild(_qr);
            var side = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            side.AddThemeConstantOverride("separation", 8);
            side.AddChild(Look.Body("Scan with your phone's camera", 16));
            bool tailscale = s.Address != null && Tailscale(s.Address);
            if (tailscale && !remote)
            {
                side.AddChild(Wrap("Tailscale: this works at home and away (mobile data, other Wi-Fi) while Tailscale is on on this PC and on the phone.", new Color(0.56f, 0.82f, 0.54f)));
                side.AddChild(Wrap("Or open this address in the phone's browser:", Look.Grey));
                _link = Look.Body(s.Url, 16);
                _link.Modulate = Look.Gold;
                side.AddChild(_link);
                var code = Look.Body("Code: " + s.Code, 16);
                code.Modulate = Look.Gold;
                side.AddChild(code);
            }
            else if (remote)
            {
                side.AddChild(Wrap("This code works at home and away (mobile data, other Wi-Fi). The phone stays paired: next time just open the page again "
                                   + "(add it to the home screen).", Look.Grey));
            }
            else
            {
                side.AddChild(Wrap("Or open this address in the phone's browser:", Look.Grey));
                _link = Look.Body(s.Url, 16);
                _link.Modulate = Look.Gold;
                side.AddChild(_link);
                var code = Look.Body("Code: " + s.Code, 16);
                code.Modulate = Look.Gold;
                side.AddChild(code);
            }
            side.AddChild(Wrap("A code pairs one phone, then a new one appears here (codes last 10 minutes).", Look.Grey));
            Button again = Look.Push("New code");
            again.Pressed += () => { s.NewCode(); Build(); };
            side.AddChild(again);
            row.AddChild(side);
            _body.AddChild(row);

            var controls = new CheckBox { Text = "Allow phone controls (without it the phone only watches)", ButtonPressed = s.ControlsAllowed, FocusMode = Control.FocusModeEnum.None };
            controls.Toggled += on => _mod.SetControls(on);
            _body.AddChild(controls);

            var auto = new CheckBox
            {
                Text = "Turn on by itself when the game starts (paired phones stay paired)", ButtonPressed = _mod.Settings.AutoStart || _mod.Settings.Remote,
                Disabled = _mod.Settings.Remote, FocusMode = Control.FocusModeEnum.None,
                TooltipText = "For using the phone away from the PC: after a restart of the game the phone finds it again without a new QR code.",
            };
            auto.Toggled += on => { _mod.SetAutoStart(on); Build(); };
            _body.AddChild(auto);
            if (_mod.Remembers)
            {
                var keep = new CheckBox { Text = "Keep phone controls on when the game restarts", ButtonPressed = _mod.Settings.KeepControls, FocusMode = Control.FocusModeEnum.None };
                keep.Toggled += on => { _mod.Settings.KeepControls = on; _mod.SetControls(s.ControlsAllowed); };
                _body.AddChild(keep);
            }

            // away from home through a relay (only when one is set: see README)
            if (_mod.RelayUrl != "")
            {
                var far = new CheckBox
                {
                    Text = "Reach from anywhere through the relay (end-to-end encrypted)", ButtonPressed = _mod.Settings.Remote, FocusMode = Control.FocusModeEnum.None,
                    TooltipText = "The game connects out to the relay; nothing to set up on your router.",
                };
                far.Toggled += on => { _mod.SetRemote(on); Build(); };
                _body.AddChild(far);
                if (_mod.Settings.Remote)
                {
                    _relay = Look.Caption(RelayText());
                    _body.AddChild(_relay);
                }
            }
            var who = new HBoxContainer();
            _people = Look.Caption("");
            _people.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            who.AddChild(_people);
            Button out_ = Look.Push("Sign out all phones");
            out_.Pressed += () => { _mod.SignOutAll(); Build(); };
            who.AddChild(out_);
            _body.AddChild(who);
        }
        // (before Start: the address picked in the list)
        bool far2 = _mod.Settings.Remote || (s.Running ? s.Address != null && Tailscale(s.Address) : choices.Count > 0 && Tailscale(choices[chosen].Ip));
        bool hasTailscale = choices.Any(c => Tailscale(c.Ip));
        _body.AddChild(Wrap(far2
            ? "Away from home: the PC must stay on and awake with the game running. If the phone cannot open the page, Windows Firewall may be blocking the game: "
              + "allow Melvor Idle 2 on private and public networks."
            : "Phone and PC must be on the same Wi-Fi (not a guest network, no VPN). If the phone cannot open the page, Windows Firewall may be "
              + "blocking the game: allow Melvor Idle 2 on private networks. Plain web page on your home network: do not use it on public Wi-Fi.", Look.Grey));
        if (!hasTailscale && !_mod.Settings.Remote)
            _body.AddChild(Wrap("Away from home too (mobile data, work): install the free Tailscale app on this PC and on the phone, sign in with the same account "
                                + "on both, then pick \"" + TailscaleName + "\" above. See README.", Look.Grey));
        Tick();
    }

    string RelayText()
    {
        RelayLink r = _mod.Relay;
        if (!r.Running) return "Relay: off (it starts with the phone screen).";
        return r.Online ? $"Relay: online. Phones connected through it: {r.Phones}" : "Relay: " + r.Status;
    }

    static Label Wrap(string text, Color? colour = null)
    {
        Label l = Look.Body(text, 13);
        l.Modulate = colour ?? Look.Cream;
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(700, 0);
        return l;
    }

    /// <summary>The QR code as a picture: black on white with a quiet border, sharp pixels.</summary>
    public static ImageTexture QrTexture(string text)
    {
        bool[,] m = Qr.Encode(text);
        int n = m.GetLength(0), scale = 6, quiet = 4, size = (n + 2 * quiet) * scale;
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgb8);
        image.Fill(Colors.White);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (m[y, x]) image.FillRect(new Rect2I((x + quiet) * scale, (y + quiet) * scale, scale, scale), Colors.Black);
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>The name a Tailscale address is listed under (the phone reaches it from anywhere).</summary>
    public const string TailscaleName = "Tailscale: works anywhere";

    /// <summary>
    /// The PC's addresses the phone could reach: Tailscale's first (it works at home and away), then the home network,
    /// virtual adapters last, then this PC only.
    /// </summary>
    public static List<(IPAddress Ip, string Name)> Addresses()
    {
        var adapters = new List<(string Name, string Description, bool Gateway, IPAddress Ip)>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props = nic.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (UnicastIPAddressInformation a in props.UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) adapters.Add((nic.Name, nic.Description, gateway, a.Address));
            }
        }
        catch { }
        return Ranked(adapters);
    }

    /// <summary>The choices made from the adapters (on its own, so a test can hand it a Tailscale adapter).</summary>
    public static List<(IPAddress Ip, string Name)> Ranked(IEnumerable<(string Name, string Description, bool Gateway, IPAddress Ip)> adapters)
    {
        var found = new List<(IPAddress Ip, string Name, int Rank)>();
        foreach (var (name, description, gateway, ip) in adapters)
        {
            string label = (name + " " + description).ToLowerInvariant();
            if (Tailscale(ip) && (label.Contains("tailscale") || !Private(ip)))
            {
                found.Add((ip, TailscaleName, -1));
                continue;
            }
            if (!Private(ip)) continue;
            bool virtualOne = new[] { "vethernet", "wsl", "hyper-v", "virtualbox", "vmware", "docker", "vpn", "tap", "tailscale", "zerotier", "loopback", "bluetooth", "mullvad", "hamachi", "radmin" }.Any(label.Contains);
            found.Add((ip, name, (gateway ? 0 : 2) + (virtualOne ? 4 : 0)));
        }
        var list = found.OrderBy(f => f.Rank).ThenBy(f => f.Name).Select(f => (f.Ip, f.Name)).ToList();
        list.Add((IPAddress.Loopback, "this PC only"));
        return list;
    }

    /// <summary>Tailscale gives each device an address in 100.64.0.0/10.</summary>
    public static bool Tailscale(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    static bool Private(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
