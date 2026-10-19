using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// VR Pinball Lobby. Two switchable views of your tables:
///   ring – one row of cards around you, the selected one in front
///   wall – a 3-row grid on a curved wall, many tables at once
/// An A–Z rail (or manufacturer / decade rail) in front of you jumps between groups.
/// Without a headset it runs in a window with the keyboard, so it can be tried on a desktop.
/// </summary>
public partial class Main : Node3D
{
    const float Height = 1.35f;
    // Ring
    const float Radius = 2.4f;
    static readonly float Step = Mathf.DegToRad(28f);     // a 1 m card spans ~24° at this radius, plus a gap
    const int RingSide = 5;                                // cards shown either side of the selection
    // Wall
    public const int Rows = 3;
    const float WallR = 3.2f;
    static readonly float ColStep = Mathf.DegToRad(19f);
    static readonly float[] WallY = [2.05f, 1.38f, 0.71f];
    const int WallCols = 4;                                // columns shown either side of the selected column
    // Jump rail (a curved strip at waist height, like a lockbar)
    const float RailR = 1.45f;
    const float RailY = 0.8f;

    public AppSettings Settings { get; private set; } = null!;
    public LobbyState State { get; private set; } = null!;
    public List<LobbyTable> Library { get; private set; } = [];
    public List<LobbyTable> Shown { get; private set; } = [];
    public List<LobbyGroup> Groups { get; private set; } = [];
    public List<TableCard> Cards { get; } = [];
    public int Selected { get; private set; }
    public int RailItemCount => _railItems.Count;

    readonly ArtLoader _art = new();
    readonly Node3D _carousel = new();
    readonly Node3D _rail = new();
    readonly List<(Label3D Label, int Group)> _railItems = [];
    readonly Label3D _header = new(), _status = new(), _hint = new(), _flash = new();
    readonly Dictionary<string, double> _held = new() { ["x"] = 0, ["jump"] = 0, ["row"] = 0 };
    readonly Dictionary<string, double> _cool = new() { ["x"] = 0, ["jump"] = 0, ["row"] = 0 };

    // Height adjustment: the player's rig (XR origin, or the desktop camera's parent) is raised or
    // lowered by State.HeightOffset. Moving the rig rather than the room keeps every layout constant
    // as-is, and recentering (which moves the tracking origin, not this node) never touches it.
    Node3D _rig = null!;
    readonly Label3D _heightLabel = new();
    double _heightShow, _statsCheck = 2.0;
    bool _heightDirty;

    bool _vr;
    float _scroll, _scrollCol;
    int _hoverCard = -1, _hoverRail = -1;
    double _hoverTime, _flashTime;
    bool _launching;
    string? _settingsPath, _statePath;
    XRController3D? _left, _right;
    RayCast3D? _ray;

    public override void _Ready()
    {
        SetupView();
        BuildRoom();
        AddChild(_art);
        _art.Loaded += OnArtLoaded;
        AddChild(_carousel);
        AddChild(_rail);

        var args = UserArgs();
        // --settings / --state (or VRLOBBY_SETTINGS / VRLOBBY_STATE) point elsewhere; used by the self-test.
        _settingsPath = args.GetValueOrDefault("settings") ?? NullIfEmpty(System.Environment.GetEnvironmentVariable("VRLOBBY_SETTINGS"));
        _statePath = args.GetValueOrDefault("state") ?? NullIfEmpty(System.Environment.GetEnvironmentVariable("VRLOBBY_STATE"));
        var selfTest = args.ContainsKey("selftest");
        if (selfTest && _settingsPath == null)
        {
            var fx = Tests.LobbyFixture.Create(Path.Combine(OS.GetUserDataDir(), "selftest-fixture"));
            _settingsPath = fx.Settings;
            _statePath = Path.Combine(fx.Root, "state.json");
        }
        Settings = _settingsPath != null ? AppSettings.LoadFrom(_settingsPath) : AppSettings.Load();
        State = LobbyState.Load(_statePath);
        ApplyHeight(); // the lobby restarts after every table, so this is where the saved height comes back
        Library = LobbyLibrary.Load(Settings);
        Rebuild(args.GetValueOrDefault("select", ""));

        if (selfTest)
            Callable.From(() => { _ = SelfTest.RunAsync(this); }).CallDeferred();
    }

    // ---- Setup -------------------------------------------------------------------

    void SetupView()
    {
        var xr = XRServer.FindInterface("OpenXR") as OpenXRInterface;
        _vr = xr != null && xr.IsInitialized();
        if (_vr)
        {
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); // the headset paces frames
            xr!.SessionStopping += () => GetTree().Quit();
            // Holding the Quest's Oculus button recenters. keep_height = true leaves the floor where the
            // headset put it (project.godot asks for the Local Floor reference space), and the user's
            // height offset lives on the rig node, so recentering can't wipe it.
            xr.PoseRecentered += () => XRServer.CenterOnHmd(XRServer.RotationMode.ResetButKeepTilt, true);

            var origin = new XROrigin3D();
            AddChild(origin);
            _rig = origin;
            var cam = new XRCamera3D();
            origin.AddChild(cam);
            AttachHeightLabel(cam);
            _left = MakeController(origin, "left_hand");
            _right = MakeController(origin, "right_hand");
            _ray = new RayCast3D { TargetPosition = new Vector3(0, 0, -6), CollideWithAreas = true, CollideWithBodies = false };
            _right.AddChild(_ray);
            _right.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.004f, 0.004f, 3f) },
                Position = new Vector3(0, 0, -1.5f),
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(1f, 0.8f, 0.3f, 0.6f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
            });
        }
        else
        {
            _rig = new Node3D();
            AddChild(_rig);
            var cam = new Camera3D { Position = new Vector3(0, 1.6f, 0.6f), Rotation = new Vector3(Mathf.DegToRad(-6), 0, 0), Fov = 70 };
            _rig.AddChild(cam);
            AttachHeightLabel(cam);
        }
    }

    /// <summary>The "Height +12 cm" readout rides on the camera so it's always in view while adjusting.</summary>
    void AttachHeightLabel(Node3D cam)
    {
        _heightLabel.PixelSize = 0.0008f;
        _heightLabel.FontSize = 56;
        _heightLabel.OutlineSize = 14;
        _heightLabel.NoDepthTest = true;
        _heightLabel.RenderPriority = 20;
        _heightLabel.Position = new Vector3(0, -0.12f, -0.7f);
        _heightLabel.Modulate = new Color(1, 1, 1, 0);
        cam.AddChild(_heightLabel);
    }

    // ---- Height ------------------------------------------------------------------

    public float RigHeight => _rig.Position.Y;

    void ApplyHeight() => _rig.Position = new Vector3(0, (float)State.HeightOffset, 0);

    void ShowHeight()
    {
        _heightLabel.Text = LobbyHeight.Format(State.HeightOffset);
        _heightShow = 1.5;
    }

    /// <summary>Sets the offset (clamped), moves the rig and shows the readout. Saving is batched (see SaveHeight).</summary>
    public void SetHeight(double offset)
    {
        State.HeightOffset = LobbyHeight.Clamp(offset);
        ApplyHeight();
        ShowHeight();
        _heightDirty = true;
    }

    public void SaveHeight()
    {
        if (!_heightDirty) return;
        State.Save();
        _heightDirty = false;
    }

    public void ResetHeight()
    {
        SetHeight(0);
        SaveHeight();
    }

    XRController3D MakeController(XROrigin3D origin, string tracker)
    {
        // Aim pose follows where the controller points, so the laser feels natural.
        var c = new XRController3D { Tracker = tracker, Pose = "aim" };
        origin.AddChild(c);
        c.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.035f, 0.035f, 0.12f) } });
        c.ButtonPressed += name => OnButton(name, c);
        return c;
    }

    void BuildRoom()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.02f, 0.02f, 0.05f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.25f, 0.25f, 0.35f),
                GlowEnabled = true,
            },
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(30, 30) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.06f, 0.06f, 0.09f), Metallic = 0.6f, Roughness = 0.35f },
        });
        // A glowing ring on the floor under the cards.
        var blue = new Color(0.3f, 0.5f, 1f);
        AddChild(new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = Radius - 0.02f, OuterRadius = Radius + 0.02f },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = blue, EmissionEnabled = true, Emission = blue, EmissionEnergyMultiplier = 2f,
            },
            Position = new Vector3(0, 0.01f, 0),
        });
        AddChild(new SpotLight3D
        {
            Position = new Vector3(0, 3.5f, -Radius + 0.6f),
            Rotation = new Vector3(Mathf.DegToRad(-80), 0, 0),
            SpotRange = 6, LightEnergy = 3,
        });

        foreach (var label in new[] { _header, _status, _hint, _flash })
        {
            label.PixelSize = 0.0012f;
            label.OutlineSize = 12;
            label.Width = 1600;
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            AddChild(label);
        }
        // Header and status grow upwards from their anchor, so extra lines never cover the cards.
        _header.FontSize = 72;
        _header.VerticalAlignment = VerticalAlignment.Bottom;
        _status.FontSize = 44;
        _status.VerticalAlignment = VerticalAlignment.Bottom;
        _status.Modulate = new Color(1f, 0.8f, 0.4f);
        _hint.FontSize = 30;
        _hint.Width = 1100;
        _hint.Modulate = new Color(1, 1, 1, 0.55f);
        _hint.VerticalAlignment = VerticalAlignment.Top;
        _hint.Position = new Vector3(0, RailY - 0.12f, -RailR + 0.12f); // just under the rail, a little closer
        _hint.Rotation = new Vector3(Mathf.DegToRad(-35), 0, 0);
        _hint.Text = _vr
            ? "Stick ← → browse  ·  ↑ ↓ jump  ·  click stick: ring / wall  ·  Trigger or A: play  ·  Right grip: favourite  ·  B: filter  ·  Menu: quit\n"
              + "Height: hold left grip + left stick ↑ ↓  ·  left grip + click left stick: reset"
            : "← → browse  ·  ↑ ↓ / PgUp PgDn / type a letter: jump  ·  V: ring / wall  ·  F: favourite  ·  Tab: filter  ·  Enter: play  ·  Esc: quit\n"
              + "Height: + / −  ·  Home: reset";
        _flash.FontSize = 260;
        _flash.OutlineSize = 40;
        _flash.NoDepthTest = true;
        _flash.RenderPriority = 10;
        _flash.Modulate = new Color(1, 1, 1, 0);
    }

    // ---- Library / layout ----------------------------------------------------------

    public void Rebuild(string selectId = "")
    {
        foreach (var c in Cards) c.QueueFree();
        Cards.Clear();
        var view = LobbyNav.Apply(Library, State.Filter, State);
        Shown = view.List;
        Groups = view.Groups;
        for (var i = 0; i < Shown.Count; i++)
        {
            var card = new TableCard();
            card.Setup(i, Shown[i]);
            card.SetFavourite(State.IsFavourite(Shown[i].Id));
            _carousel.AddChild(card);
            Cards.Add(card);
        }

        selectId = LobbyLibrary.NormalizeId(selectId);
        Selected = Math.Max(0, Shown.FindIndex(t => string.Equals(t.Id, selectId, StringComparison.OrdinalIgnoreCase)));
        _scroll = Selected;
        _scrollCol = Selected / Rows;
        PlaceLabels();
        BuildRail();
        UpdateSelection();
        Layout();
    }

    bool IsWall => State.View == "wall";

    void PlaceLabels()
    {
        if (IsWall)
        {
            _header.Position = new Vector3(0, 2.78f, -WallR + 0.3f);
            _status.Position = new Vector3(0, 2.46f, -WallR + 0.3f);
            _flash.Position = new Vector3(0, 1.4f, -WallR + 0.8f);
        }
        else
        {
            _header.Position = new Vector3(0, Height + 0.82f, -Radius + 0.35f);
            _status.Position = new Vector3(0, Height + 0.52f, -Radius + 0.35f);
            _flash.Position = new Vector3(0, Height, -Radius + 0.9f);
        }
    }

    void Layout()
    {
        foreach (var c in Cards)
        {
            if (IsWall) LayoutWall(c);
            else LayoutRing(c);
        }
    }

    void LayoutRing(TableCard c)
    {
        var offset = c.Index - _scroll;
        c.Visible = Mathf.Abs(offset) <= RingSide;
        if (Mathf.Abs(offset) <= RingSide + 2) _art.Request(c.Table.ImgUrl);
        if (!c.Visible) return;
        var a = offset * Step;
        var focus = Mathf.Clamp(1f - Mathf.Abs(offset), 0f, 1f); // 1 for the selected card
        var r = Radius - 0.35f * focus;
        c.Position = new Vector3(Mathf.Sin(a) * r, Height + 0.08f * focus, -Mathf.Cos(a) * r);
        c.Rotation = new Vector3(0, -a, 0);
        c.Scale = Vector3.One * (1f + 0.3f * focus);
    }

    void LayoutWall(TableCard c)
    {
        // Column-major, so alphabetical order runs down a column and then to the right.
        var colOffset = c.Index / Rows - _scrollCol;
        c.Visible = Mathf.Abs(colOffset) <= WallCols;
        if (Mathf.Abs(colOffset) <= WallCols + 2) _art.Request(c.Table.ImgUrl);
        if (!c.Visible) return;
        var a = colOffset * ColStep;
        var selected = c.Index == Selected;
        var r = WallR - (selected ? 0.2f : 0f);
        c.Position = new Vector3(Mathf.Sin(a) * r, WallY[c.Index % Rows], -Mathf.Cos(a) * r);
        c.Rotation = new Vector3(0, -a, 0);
        c.Scale = Vector3.One * (selected ? 1f : 0.9f);
    }

    // ---- Jump rail -------------------------------------------------------------------

    bool LetterRail => Groups.Count == LobbyNav.Letters.Length;

    void BuildRail()
    {
        foreach (var (label, _) in _railItems) label.QueueFree();
        _railItems.Clear();
        if (Groups.Count == 0) return;
        var current = LobbyNav.GroupAt(Groups, Selected);
        // Letters all fit; long lists (manufacturers) show a window around the current one.
        int first = 0, count = Groups.Count;
        if (!LetterRail && count > 9)
        {
            first = Math.Clamp(current - 4, 0, count - 9);
            count = 9;
        }
        var spread = Mathf.DegToRad(LetterRail ? 52f : 56f);
        for (var k = 0; k < count; k++)
        {
            var g = first + k;
            var t = count == 1 ? 0.5f : (float)k / (count - 1);
            var a = Mathf.Lerp(-spread, spread, t);
            var label = new Label3D
            {
                Text = Groups[g].Label,
                FontSize = LetterRail ? 64 : 36,
                PixelSize = 0.0012f,
                OutlineSize = 10,
                Position = new Vector3(Mathf.Sin(a) * RailR, RailY, -Mathf.Cos(a) * RailR),
                Rotation = new Vector3(Mathf.DegToRad(-35), -a, 0),
            };
            var area = new Area3D();
            area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(LetterRail ? 0.09f : 0.22f, 0.09f, 0.02f) } });
            area.SetMeta("rail_group", g);
            label.AddChild(area);
            _rail.AddChild(label);
            _railItems.Add((label, g));
        }
        UpdateRail();
    }

    void UpdateRail()
    {
        var current = LobbyNav.GroupAt(Groups, Selected);
        if (_railItems.Count > 0 && !LetterRail)
        {
            // Windowed rail: rebuild when the current group reaches the edge of the window.
            var firstShown = _railItems[0].Group;
            var lastShown = _railItems[^1].Group;
            if (current < firstShown || current > lastShown || (current == firstShown && current > 0) || (current == lastShown && current < Groups.Count - 1))
            {
                Callable.From(BuildRail).CallDeferred();
                return;
            }
        }
        foreach (var (label, g) in _railItems)
        {
            if (g == current)
            {
                label.Modulate = new Color(1f, 0.8f, 0.25f);
                label.Scale = Vector3.One * 1.35f;
            }
            else if (Groups[g].Index == -1)
            {
                label.Modulate = new Color(1, 1, 1, 0.18f);
                label.Scale = Vector3.One;
            }
            else
            {
                label.Modulate = new Color(1, 1, 1, g == _hoverRail ? 1f : 0.75f);
                label.Scale = Vector3.One * (g == _hoverRail ? 1.2f : 1f);
            }
        }
    }

    public void JumpToGroup(int g)
    {
        if (g < 0 || g >= Groups.Count) return;
        Select(LobbyNav.IndexForGroup(Groups, g));
        ShowFlash();
    }

    public void JumpToKey(char key) => JumpToGroup(LobbyNav.GroupForKey(Groups, key));

    void ShowFlash()
    {
        var g = LobbyNav.GroupAt(Groups, Selected);
        if (g == -1) return;
        _flash.Text = Groups[g].Label;
        _flash.FontSize = Groups[g].Label.Length <= 2 ? 260 : 120;
        _flashTime = 0.7;
    }

    // ---- Selection ------------------------------------------------------------------

    void UpdateSelection()
    {
        foreach (var c in Cards) c.SetSelected(c.Index == Selected);
        UpdateRail();
        if (Shown.Count == 0)
        {
            _header.Text = "No tables here";
            _status.Text = LobbyText.Empty(State.Filter, Library.Count);
            _status.Modulate = new Color(1f, 0.8f, 0.4f);
            return;
        }
        var t = Shown[Selected];
        _header.Text = (State.IsFavourite(t.Id) ? "★ " : "") + t.Title;
        var where = $"{Selected + 1} / {Shown.Count}  ·  {State.Filter}";
        var g = LobbyNav.GroupAt(Groups, Selected);
        if (g != -1 && State.Filter is "Manufacturer" or "Decade") where += ": " + Groups[g].Label;
        // Shared with the manager: plays and time are counted by VrTableManager --play.
        var lines = new List<string> { t.Subtitle, LobbyText.Stats(State.Stats, t.Id, DateTime.Now), where };
        if (t.MissingRoms.Count > 0) lines.Add("Needs ROM: " + string.Join(", ", t.MissingRoms) + ".zip (add it in VR Table Manager)");
        _status.Text = string.Join("\n", lines);
        _status.Modulate = t.MissingRoms.Count > 0 ? new Color(1f, 0.55f, 0.45f) : new Color(1f, 0.8f, 0.4f);
    }

    public void Select(int i)
    {
        if (Shown.Count == 0) return;
        var before = Selected;
        Selected = Math.Clamp(i, 0, Shown.Count - 1);
        if (Selected == before) return;
        UpdateSelection();
        if (IsWall) Layout(); // selection pop-out is instant; the column scroll animates in _Process
        _left?.TriggerHapticPulse("haptic", 0.0, 0.3, 0.05, 0.0);
        _right?.TriggerHapticPulse("haptic", 0.0, 0.3, 0.05, 0.0);
    }

    public void ToggleView()
    {
        State.View = IsWall ? "ring" : "wall";
        State.Save();
        _scroll = Selected;
        _scrollCol = Selected / Rows;
        PlaceLabels();
        Layout();
    }

    public void CycleFilter()
    {
        var current = Shown.Count > 0 ? Shown[Selected].Id : "";
        State.Filter = LobbyNav.Filters[(Array.IndexOf(LobbyNav.Filters, State.Filter) + 1) % LobbyNav.Filters.Length];
        State.Save();
        Rebuild(current);
    }

    public string HeaderText => _header.Text;
    public string StatusText => _status.Text;

    static bool IsStatsFilter(string filter) => filter is "Favourites" or "Recently played" or "Most played";

    /// <summary>
    /// Picks up favourites / plays changed by the manager (or the --play launcher) while the lobby is open.
    /// Returns true when the shared stats file had changed.
    /// </summary>
    public bool RefreshFromStats()
    {
        if (!State.RefreshStats()) return false;
        foreach (var c in Cards) c.SetFavourite(State.IsFavourite(c.Table.Id));
        if (IsStatsFilter(State.Filter)) Rebuild(Shown.Count > 0 ? Shown[Selected].Id : "");
        else UpdateSelection();
        return true;
    }

    public void ToggleFavourite()
    {
        if (Shown.Count == 0) return;
        var t = Shown[Selected];
        var on = State.ToggleFavourite(t.Id);
        Cards[Selected].SetFavourite(on);
        if (State.Filter == "Favourites" && !on)
            Rebuild(Shown[Math.Min(Selected + 1, Shown.Count - 1)].Id);
        else
            UpdateSelection();
    }

    // ---- Input ---------------------------------------------------------------------

    public override void _Process(double delta)
    {
        var d = (float)delta;
        float targetCol = Selected / Rows;
        var moved = false;
        if (!Mathf.IsEqualApprox(_scroll, Selected))
        {
            _scroll = Mathf.Lerp(_scroll, Selected, 1f - Mathf.Exp(-12f * d));
            if (Mathf.Abs(_scroll - Selected) < 0.001f) _scroll = Selected;
            moved = !IsWall;
        }
        if (!Mathf.IsEqualApprox(_scrollCol, targetCol))
        {
            _scrollCol = Mathf.Lerp(_scrollCol, targetCol, 1f - Mathf.Exp(-10f * d));
            if (Mathf.Abs(_scrollCol - targetCol) < 0.001f) _scrollCol = targetCol;
            moved |= IsWall;
        }
        if (moved) Layout();
        if (_flashTime > 0)
        {
            _flashTime -= delta;
            _flash.Modulate = new Color(1, 1, 1, Mathf.Clamp((float)_flashTime / 0.3f, 0f, 0.85f));
        }
        _statsCheck -= delta;
        if (_statsCheck <= 0)
        {
            _statsCheck = 2.0; // a file-time check every 2 s; only re-reads when the file changed
            RefreshFromStats();
        }
        if (_heightShow > 0)
        {
            _heightShow -= delta;
            _heightLabel.Modulate = new Color(1, 1, 1, Mathf.Clamp((float)_heightShow / 0.4f, 0f, 1f));
        }

        if (!_vr || _launching || _left == null || _right == null || _ray == null) return;

        var lv = _left.GetVector2("primary");
        var rv = _right.GetVector2("primary");

        // Height: while the left grip is held, the left stick's up/down raises or lowers you smoothly
        // (and does nothing else). Saved once you let go, so the file isn't rewritten every frame.
        if (_left.GetFloat("grip") > 0.6f)
        {
            var next = LobbyHeight.Step(State.HeightOffset, lv.Y, delta);
            if (next != State.HeightOffset) SetHeight(next);
            else if (_heightShow < 0.5) ShowHeight(); // keep the readout up while gripping
            lv = Vector2.Zero;
        }
        else SaveHeight();

        // Right stick ← → browses (accelerating while held). Up/down on the left stick jumps
        // between rail groups; the right stick's up/down does the same in the ring and moves
        // between rows on the wall.
        var step = Stick("x", Strongest(lv.X, rv.X), delta);
        if (step != 0)
        {
            Select(Selected + step * (IsWall ? Rows : 1));
            if (_held["x"] > 0.6) ShowFlash(); // moving fast: show which letter we're passing
        }
        var j = Stick("jump", IsWall ? lv.Y : Strongest(lv.Y, rv.Y), delta);
        if (j != 0)
        {
            Select(LobbyNav.Jump(Groups, Selected, -j)); // stick up = earlier letter
            ShowFlash();
        }
        if (IsWall)
        {
            var row = Stick("row", rv.Y, delta, accelerate: false);
            if (row != 0) Select(Selected - row);
        }

        // Pointing: at a card for a moment selects it; at the rail highlights a group.
        int card = -1, rail = -1;
        if (_ray.IsColliding() && _ray.GetCollider() is GodotObject hit)
        {
            if (hit.HasMeta("card_index")) card = hit.GetMeta("card_index").AsInt32();
            else if (hit.HasMeta("rail_group")) rail = hit.GetMeta("rail_group").AsInt32();
        }
        if (rail != _hoverRail)
        {
            _hoverRail = rail;
            UpdateRail();
        }
        if (card != _hoverCard)
        {
            _hoverCard = card;
            _hoverTime = 0;
        }
        else if (card != -1 && card != Selected)
        {
            _hoverTime += delta;
            if (_hoverTime > 0.25) Select(card);
        }
    }

    /// <summary>-1/0/+1 for a stick axis with a dead zone; repeats while held, faster when accelerate is set.</summary>
    int Stick(string channel, float value, double delta, bool accelerate = true)
    {
        if (Mathf.Abs(value) < 0.3f)
        {
            _held[channel] = 0;
            _cool[channel] = 0;
            return 0;
        }
        if (Mathf.Abs(value) < 0.6f) return 0;
        _held[channel] += delta;
        _cool[channel] -= delta;
        if (_cool[channel] > 0) return 0;
        _cool[channel] = accelerate ? LobbyNav.RepeatInterval(_held[channel]) : 0.45;
        return value > 0 ? 1 : -1;
    }

    static float Strongest(float a, float b) => Mathf.Abs(a) >= Mathf.Abs(b) ? a : b;

    void OnButton(string button, XRController3D controller)
    {
        if (_launching) return;
        switch (button)
        {
            case "trigger_click" or "ax_button":
                if (controller == _right && _hoverRail != -1)
                {
                    JumpToGroup(_hoverRail);
                    return;
                }
                if (controller == _right && _hoverCard != -1) Select(_hoverCard);
                _ = LaunchAsync();
                break;
            case "by_button": CycleFilter(); break;
            // The left grip is the height "shift" key, so only the right grip favourites.
            case "grip_click" when controller == _right: ToggleFavourite(); break;
            case "primary_click" when controller == _left && _left!.GetFloat("grip") > 0.6f: ResetHeight(); break;
            case "primary_click": ToggleView(); break;
            case "menu_button": GetTree().Quit(); break;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_launching || @event is not InputEventKey { Pressed: true } k) return;
        switch (k.Keycode)
        {
            case Key.Right: Select(Selected + (IsWall ? Rows : 1)); break;
            case Key.Left: Select(Selected - (IsWall ? Rows : 1)); break;
            case Key.Down when IsWall: Select(Selected + 1); break;
            case Key.Up when IsWall: Select(Selected - 1); break;
            case Key.Down or Key.Pagedown: Select(LobbyNav.Jump(Groups, Selected, 1)); ShowFlash(); break;
            case Key.Up or Key.Pageup: Select(LobbyNav.Jump(Groups, Selected, -1)); ShowFlash(); break;
            case Key.Enter or Key.KpEnter or Key.Space: _ = LaunchAsync(); break;
            case Key.Escape: GetTree().Quit(); break;
            case Key.Tab: CycleFilter(); break;
            case Key.V: ToggleView(); break;
            case Key.F: ToggleFavourite(); break;
            case Key.Equal or Key.KpAdd: SetHeight(State.HeightOffset + LobbyHeight.KeyStep); SaveHeight(); break;
            case Key.Minus or Key.KpSubtract: SetHeight(State.HeightOffset - LobbyHeight.KeyStep); SaveHeight(); break;
            case Key.Home: ResetHeight(); break;
            default:
                if (k.Unicode is > 0 and < 128 && char.IsLetterOrDigit((char)k.Unicode)) JumpToKey((char)k.Unicode);
                break;
        }
    }

    // ---- Art / launch --------------------------------------------------------------

    void OnArtLoaded(string url, Texture2D tex)
    {
        foreach (var c in Cards)
            if (c.Table.ImgUrl == url) c.SetTexture(tex);
    }

    async Task LaunchAsync()
    {
        if (Shown.Count == 0) return;
        var t = Shown[Selected];
        if (LobbyLauncher.CheckReady(t, Settings) is { } problem)
        {
            ShowError(problem);
            return;
        }
        _launching = true;
        _header.Text = "Starting " + t.Title;
        _status.Text = "The lobby closes now and comes back when you exit the table.";
        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);
        if (LobbyLauncher.Launch(t, Settings, _settingsPath, _statePath) is { } error)
        {
            _launching = false;
            ShowError(error);
            return;
        }
        State.MarkPlayed(t.Id);
        GetTree().Quit(); // ends our OpenXR session so the table gets the headset
    }

    void ShowError(string message)
    {
        _status.Text = message;
        _status.Modulate = new Color(1f, 0.45f, 0.4f);
    }

    static Dictionary<string, string> UserArgs()
    {
        var d = new Dictionary<string, string>();
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (!a.StartsWith("--")) continue;
            var eq = a.IndexOf('=');
            if (eq > 2) d[a[2..eq]] = a[(eq + 1)..];
            else d[a[2..]] = "";
        }
        return d;
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
