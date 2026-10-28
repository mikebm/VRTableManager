using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

namespace VrLobby;

/// <summary>
/// VR Pinball Lobby: your tables as 3D pinball cabinets standing in a ring around you, in a dark
/// room with neon trim. The selected cabinet steps in front of you under a spotlight; the rest
/// wait around the ring, angled away and dimmed. An A–Z rail (or manufacturer / decade rail) at
/// knee height jumps between groups. Without a headset it runs in a window with the keyboard.
/// </summary>
public partial class Main : Node3D
{
    // Ring of cabinets (distances are to the cabinet's front edge, from the player standing at the origin)
    static readonly float Step = Mathf.DegToRad(34f);  // room for a 0.74 m cabinet plus a gap at the ring distance
    const int Side = 5;                                 // cabinets either side of the selected one
    public const int PoolSize = Side * 2 + 1;           // only these 11 cabinet nodes ever exist
    public const float RingFront = 2.1f;                // the ring
    public const float SelectedFront = 0.9f;            // the selected cabinet steps up to about arm's length
    static readonly float AngleAway = Mathf.DegToRad(12f);
    // Jump rail: a curved console at knee height, just in front of the selected cabinet
    const float RailR = 0.8f;
    const float RailY = 0.5f;
    static readonly float RailTilt = Mathf.DegToRad(-55f);

    public AppSettings Settings { get; private set; } = null!;
    public LobbyState State { get; private set; } = null!;
    public List<LobbyTable> Library { get; private set; } = [];
    public List<LobbyTable> Shown { get; private set; } = [];
    public List<LobbyGroup> Groups { get; private set; } = [];
    public IReadOnlyList<Cabinet> Cabinets => _pool;
    public int Selected { get; private set; }
    public int RailItemCount => _railItems.Count;
    public bool BackglassIndexReady => _backglass != null;
    public string? AttractPlaying { get; private set; }

    readonly ArtLoader _art = new();
    readonly RealCabinetLoader _cabs = new();
    readonly List<Cabinet> _pool = [];
    readonly Node3D _ring = new();
    readonly Node3D _rail = new();
    readonly List<(Label3D Label, int Group)> _railItems = [];
    readonly Label3D _header = new(), _status = new(), _hint = new(), _flash = new(), _scores = new();
    readonly MeshInstance3D _scoresPanel = new();   // dark DMD glass behind the high scores
    readonly Dictionary<string, IReadOnlyList<string>> _scoreLines = [];   // per table id; .nv files only change while a table runs, and the lobby restarts after that
    readonly Dictionary<string, double> _held = new() { ["x"] = 0, ["jump"] = 0 };
    readonly Dictionary<string, double> _cool = new() { ["x"] = 0, ["jump"] = 0 };
    readonly AudioStreamPlayer3D _attract = new() { VolumeDb = -12f, UnitSize = 3f };
    readonly Dictionary<string, string?> _attractFiles = [];
    readonly RomRecorder _recorder = new();
    bool _recordRoms;
    Dictionary<string, string>? _backglass;
    Font? _dmdFont, _displayFont, _bodyFont;   // Doto (dot-matrix), Big Shoulders Display, Archivo

    // Height adjustment: the player's rig (XR origin, or the desktop camera's parent) is raised or
    // lowered by State.HeightOffset. Moving the rig rather than the room keeps every layout constant
    // as-is, and recentering (which moves the tracking origin, not this node) never touches it.
    Node3D _rig = null!;
    readonly Label3D _heightLabel = new();
    double _heightShow, _statsCheck = 2.0;
    bool _heightDirty;

    bool _vr;
    float _scroll;
    int _hoverSlot = -1, _hoverRail = -1;
    double _hoverTime, _flashTime, _settled, _attractLeft;
    bool _launching;
    string? _settingsPath, _statePath;
    XRController3D? _left, _right;
    RayCast3D? _ray;

    public override void _Ready()
    {
        _dmdFont = LoadFont("res://fonts/Doto-Black.ttf");
        _displayFont = LoadFont("res://fonts/BigShouldersDisplay-Black.ttf");
        _bodyFont = LoadFont("res://fonts/Archivo-SemiBold.ttf");
        SetWindowIcon();
        SetupView();
        BuildRoom();
        AddChild(_art);
        _art.Loaded += OnArtLoaded;
        AddChild(_cabs);
        _cabs.CabinetReady += OnCabinetReady;
        AddChild(_ring);
        AddChild(_rail);
        AddChild(_attract);
        AddChild(_recorder);
        _recorder.Recorded += id => _attractFiles.Remove(id); // look again: it plays if the table is still selected
        for (var s = 0; s < PoolSize; s++)
        {
            var cab = new Cabinet();
            _ring.AddChild(cab);
            cab.Build(s, _displayFont, _dmdFont);
            _pool.Add(cab);
        }

        var args = UserArgs();
        // --dump-cabinet=<table.vpx>: write that table's VR cabinet as the default cabinet, then quit.
        if (args.GetValueOrDefault("dump-cabinet") is { Length: > 0 } dumpVpx)
        {
            var ok = RealCabinetLoader.DumpDefault(dumpVpx, ProjectSettings.GlobalizePath(DefaultCabinetFile));
            GD.Print(ok ? $"Default cabinet written from {dumpVpx}" : $"No VR cabinet in {dumpVpx}");
            GetTree().Quit(ok ? 0 : 1);
            return;
        }
        // The default cabinet before any table shows, so nothing ever appears in the drawn one first.
        _template = RealCabinetLoader.LoadDefault(ProjectSettings.GlobalizePath(DefaultCabinetFile));
        if (_template == null) GD.PushWarning($"No default cabinet at {DefaultCabinetFile}: tables without their own use the drawn cabinet.");
        // --settings / --state (or VRLOBBY_SETTINGS / VRLOBBY_STATE) point elsewhere; used by the self-test.
        _settingsPath = args.GetValueOrDefault("settings") ?? NullIfEmpty(System.Environment.GetEnvironmentVariable("VRLOBBY_SETTINGS"));
        _statePath = args.GetValueOrDefault("state") ?? NullIfEmpty(System.Environment.GetEnvironmentVariable("VRLOBBY_STATE"));
        var selfTest = args.ContainsKey("selftest");
        if (selfTest && _settingsPath == null)
        {
            var fx = Tests.LobbyFixture.Create(Path.Combine(OS.GetUserDataDir(), "selftest-fixture"));
            _settingsPath = fx.Settings;
            _statePath = Path.Combine(fx.Root, "state.json");
            _cabs.CacheDir = Path.Combine(fx.Root, "cabinet-cache"); // a fresh cache each run, not the real one
        }
        Settings = _settingsPath != null ? AppSettings.LoadFrom(_settingsPath) : AppSettings.Load();
        State = LobbyState.Load(_statePath);
        ApplyHeight(); // the lobby restarts after every table, so this is where the saved height comes back
        Library = LobbyLibrary.Load(Settings);
        // --record-audio=<table id>: record that table's ROM sound (see RomRecorder), then quit.
        if (args.GetValueOrDefault("record-audio") is { Length: > 0 } recordId)
        {
            var file = Library.FirstOrDefault(x => x.Id == recordId) is { } rt
                ? RomAudio.Record(rt.Source, RomAudio.PinmameDll(Settings), m => GD.Print(m))
                : null;
            GD.Print(file != null ? $"Recorded {file}" : $"No sound recorded for {recordId}");
            GetTree().Quit(file != null ? 0 : 1);
            return;
        }
        _recordRoms = !selfTest && Settings.LobbyRecordsRomAudio;
        _recorder.SettingsPath = _settingsPath;
        _borrowAll = args.ContainsKey("borrow-all");
        Rebuild(args.GetValueOrDefault("select", ""));
        LoadBackglassIndex();
        if (!selfTest) RefreshHighScoreMaps();

        if (selfTest)
            Callable.From(() => { _ = SelfTest.RunAsync(this); }).CallDeferred();
    }

    /// <summary>
    /// Fonts are read straight from the file (no editor import step), so the lobby also works when
    /// started with `godot --path VrLobby` on a fresh checkout. Falls back to Godot's default font.
    /// </summary>
    static Font? LoadFont(string path)
    {
        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes(path);
            return bytes.Length == 0 ? null : new FontFile { Data = bytes };
        }
        catch { return null; }
    }

    /// <summary>
    /// The desktop window's icon: the manager's "VR" dot-matrix tile. Read from the raw file, like the
    /// fonts, so it works without the editor's import step (project.godot also names it for exports).
    /// </summary>
    static void SetWindowIcon()
    {
        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes("res://icon.png");
            var img = new Image();
            if (bytes.Length > 0 && img.LoadPngFromBuffer(bytes) == Error.Ok) DisplayServer.SetIcon(img);
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>Backglass pictures come from the manager's cached VPS catalog (no network); ~8 MB, so it's read in the background.</summary>
    void LoadBackglassIndex()
    {
        Task.Run(() => LobbyArt.BackglassIndex(VpsCatalog.LoadCached() ?? []))
            .ContinueWith(t =>
            {
                var index = t.IsCompletedSuccessfully ? t.Result : new Dictionary<string, string>();
                Callable.From(() =>
                {
                    _backglass = index;
                    foreach (var cab in _pool)
                        if (cab.Table != null) AssignCabinet(cab, cab.Index, requestArt: true);
                }).CallDeferred();
            });
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
                    AlbedoColor = new Color(Palette.Lamp, 0.6f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
        else
        {
            // Desktop: roughly where you would stand, tilted down a little so the cabinet, the title above it and the rail all fit.
            _rig = new Node3D();
            AddChild(_rig);
            var cam = new Camera3D { Position = new Vector3(0, 1.62f, 0.3f), Rotation = new Vector3(Mathf.DegToRad(-15), 0, 0), Fov = 80 };
            _rig.AddChild(cam);
            AttachHeightLabel(cam);
        }
    }

    /// <summary>The "Height +12 cm" readout rides on the camera so it's always in view while adjusting.</summary>
    void AttachHeightLabel(Node3D cam)
    {
        _heightLabel.PixelSize = 0.0008f;
        _heightLabel.Font = _bodyFont;
        _heightLabel.FontSize = 56;
        _heightLabel.OutlineSize = 14;
        _heightLabel.NoDepthTest = true;
        _heightLabel.RenderPriority = 20;
        _heightLabel.Position = new Vector3(0, -0.12f, -0.7f);
        _heightLabel.Modulate = new Color(1, 1, 1, 0);
        cam.AddChild(_heightLabel);
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
                BackgroundColor = Palette.Bg,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.24f, 0.24f, 0.26f),
                // The background stays flat black, but reflections come from a small generated map of
                // the room, so chrome (lockbar, rails, speaker rings) has something to mirror.
                Sky = new Sky { SkyMaterial = new PanoramaSkyMaterial { Panorama = ReflectionSky() }, RadianceSize = Sky.RadianceSizeEnum.Size128 },
                ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
                GlowEnabled = true,
                GlowIntensity = 0.7f,
                // Distant cabinets and walls fade into the dark instead of ending at a hard edge.
                FogEnabled = true,
                FogLightColor = Palette.Bg,
                FogDensity = 0.03f,
                FogSkyAffect = 0f,
            },
        });

        // Floor and far wall: neutral dark grey, so the lamps and the DMDs are the only warm light.
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(30, 30) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.045f, 0.045f, 0.05f), Metallic = 0.1f, Roughness = 0.7f },
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 7, BottomRadius = 7, Height = 4.5f, RadialSegments = 48, CapTop = false, CapBottom = false },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.055f), CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.9f },
            Position = new Vector3(0, 2.25f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Trim: rows of small incandescent lamps (like a marquee or a playfield's GI string) and chrome
        // edges, kept dim so the cabinets stay the brightest things in the room. Each row is one draw call.
        var ringOut = RingFront + CabinetKit.Length + 0.35f;
        AddChild(LampRow(ringOut, 0.02f, 72, 0.018f, 1.2f));      // on the floor behind the cabinets
        AddChild(ChromeRing(ringOut + 0.06f, 0.006f, 0.008f));     // with a chrome edge
        AddChild(ChromeRing(0.5f, 0.004f, 0.006f));                // a chrome circle round your feet
        AddChild(LampRow(6.9f, 3.0f, 120, 0.025f, 0.7f));         // one row high on the far wall

        // A warm spotlight on the selected cabinet (the only shadow-casting light), and a neutral fill.
        var spot = new SpotLight3D
        {
            SpotAngle = 30, SpotRange = 6, LightEnergy = 6, LightColor = Colors.White.Lerp(Palette.Lamp, 0.3f),
            ShadowEnabled = true, SpotAngleAttenuation = 0.6f,
            // Godot's default offsets (3 cm, plus a normal offset) let a strip of light through just under a
            // lockdown bar onto the cabinet front, which should sit in the bar's shadow.
            ShadowBias = 0.004f, ShadowNormalBias = 0.3f,
        };
        AddChild(spot);
        var cabCentre = -(SelectedFront + CabinetKit.Length / 2);
        spot.LookAtFromPosition(new Vector3(0, 3.4f, cabCentre + 0.45f), new Vector3(0, 0.9f, cabCentre), Vector3.Up);
        // Fill from behind you, so cabinet fronts and sides read; no shadows (one shadowed light is enough).
        AddChild(new OmniLight3D { Position = new Vector3(0, 2.6f, 0.6f), OmniRange = 8, LightEnergy = 1.6f, LightColor = new Color(0.92f, 0.92f, 0.95f) });

        // Title (DMD orange, dot-matrix) and stats float above the selected cabinet's backbox;
        // the hint sits under the rail.
        foreach (var label in new[] { _header, _status, _hint, _flash, _scores })
        {
            label.PixelSize = 0.0012f;
            label.OutlineSize = 12;
            label.OutlineModulate = new Color(0, 0, 0, 0.85f);
            label.Width = 1700;
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            AddChild(label);
        }
        _header.Font = _dmdFont;
        _header.FontSize = 72;
        _header.Modulate = Palette.Dmd;
        _header.OutlineModulate = Palette.DmdPanel;
        _header.VerticalAlignment = VerticalAlignment.Bottom;
        _header.Position = new Vector3(0, 2.34f, -1.8f);
        _status.Font = _bodyFont;
        _status.FontSize = 38;
        _status.VerticalAlignment = VerticalAlignment.Top;
        _status.Position = new Vector3(0, 2.29f, -1.8f);
        _status.Modulate = Palette.Text;
        // High scores read from the table's PinMAME .nv file, in DMD orange dot-matrix under the stats; placed by ShowScores.
        _scores.Font = _dmdFont;
        _scores.FontSize = 32;
        _scores.Modulate = Palette.Dmd;
        _scores.OutlineModulate = Palette.DmdPanel;
        _scores.VerticalAlignment = VerticalAlignment.Top;
        _scores.Position = _status.Position;
        _scoresPanel.Mesh = new QuadMesh();
        _scoresPanel.MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Palette.DmdPanel };
        _scoresPanel.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _scoresPanel.Visible = false;
        AddChild(_scoresPanel);
        _hint.Font = _bodyFont;
        _hint.FontSize = 26;
        _hint.Width = 1150;
        _hint.Modulate = Palette.Muted;
        _hint.VerticalAlignment = VerticalAlignment.Top;
        _hint.Position = new Vector3(0, RailY - 0.16f, -RailR + 0.3f); // under and inside the rail
        _hint.Rotation = new Vector3(RailTilt, 0, 0);
        _hint.Text = _vr
            ? "Stick ← → browse  ·  ↑ ↓ jump A–Z  ·  Trigger or A: play  ·  Y: random  ·  B: filter  ·  Right grip: favourite  ·  Menu: quit\n"
              + "Height: hold left grip + left stick ↑ ↓  ·  left grip + click left stick: reset"
            : "← → browse  ·  ↑ ↓ / PgUp PgDn / type a letter: jump  ·  Enter: play  ·  Ctrl+R or ?: random  ·  Tab: filter  ·  F: favourite  ·  Esc: quit\n"
              + "Height: + / −  ·  Home: reset";
        _flash.Font = _dmdFont;
        _flash.FontSize = 260;
        _flash.OutlineSize = 40;
        _flash.OutlineModulate = Palette.DmdPanel;
        _flash.NoDepthTest = true;
        _flash.RenderPriority = 10;
        _flash.Position = new Vector3(0, 1.45f, -1.25f);
        _flash.Modulate = new Color(Palette.Dmd, 0);
    }

    /// <summary>
    /// What the chrome reflects (an equirectangular map, top row = straight up): a dark room, a soft
    /// bright ceiling light, and a warm band of lamps near the horizon. Half-float, so the light can be
    /// brighter than white and give chrome its sharp highlights.
    /// </summary>
    static ImageTexture ReflectionSky()
    {
        const int w = 128, h = 64;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgbah);
        for (var y = 0; y < h; y++)
        {
            var v = (y + 0.5f) / h;                    // 0 = up, 0.5 = horizon, 1 = down
            var c = new Color(0.035f, 0.035f, 0.04f);    // the room
            c += new Color(1f, 0.98f, 0.95f) * (3.2f * Mathf.Exp(-Mathf.Pow((v - 0.16f) / 0.06f, 2)));   // ceiling light
            c += Palette.Lamp * (0.8f * Mathf.Exp(-Mathf.Pow((v - 0.45f) / 0.015f, 2)));                  // lamps round the room
            if (v > 0.5f) c *= 0.35f;                                                                      // dark floor
            for (var x = 0; x < w; x++) img.SetPixel(x, y, c);
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>A circle of small lit lamp bulbs (one MultiMesh, so one draw call).</summary>
    static MultiMeshInstance3D LampRow(float radius, float y, int count, float bulb, float energy)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new SphereMesh { Radius = bulb, Height = bulb * 2, RadialSegments = 8, Rings = 4 },
            InstanceCount = count,
        };
        for (var i = 0; i < count; i++)
        {
            var a = Mathf.Tau * i / count;
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(Mathf.Sin(a) * radius, y, -Mathf.Cos(a) * radius)));
        }
        return new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = Palette.Lamp, EmissionEnabled = true, Emission = Palette.Lamp, EmissionEnergyMultiplier = energy,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    static MeshInstance3D ChromeRing(float radius, float y, float thickness) => new()
    {
        Mesh = new TorusMesh { InnerRadius = radius - thickness, OuterRadius = radius + thickness, Rings = 96, RingSegments = 8 },
        MaterialOverride = CabinetKit.ChromeMaterial,
        Position = new Vector3(0, y, 0),
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

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

    // ---- Library / ring ------------------------------------------------------------

    public void Rebuild(string selectId = "")
    {
        var view = LobbyNav.Apply(Library, State.Filter, State);
        Shown = view.List;
        Groups = view.Groups;
        selectId = LobbyLibrary.NormalizeId(selectId);
        Selected = Math.Max(0, Shown.FindIndex(t => string.Equals(t.Id, selectId, StringComparison.OrdinalIgnoreCase)));
        _scroll = Selected;
        foreach (var cab in _pool) cab.Park(); // the list changed: every cabinet gets re-assigned
        BuildRail();
        UpdateSelection();
        Layout();
    }

    /// <summary>Selected cabinet's slot, or null when the list is empty.</summary>
    public Cabinet? SelectedCabinet => _pool.FirstOrDefault(c => c.Index == Selected && c.Table != null);

    void AssignCabinet(Cabinet cab, int index, bool requestArt)
    {
        var t = Shown[index];
        cab.Assign(index, t, LobbyArt.PlayfieldSource(t), LobbyArt.BackglassFor(t, _backglass), State.IsFavourite(t.Id));
        // Already read this table's own cabinet (or know it has none)? Show it straight away.
        ShowCabinetFor(cab);
        if (requestArt) RequestArt(cab);
    }

    void RequestArt(Cabinet cab)
    {
        // Playfield: the table file's own picture at up to 2048 px, else the VPS picture.
        if (cab.PlayfieldUrl != null && !cab.HasPlayfieldArt && cab.Table != null)
            _art.Request(cab.PlayfieldUrl, LobbyArt.PlayfieldUrl(cab.Table), ArtLoader.PlayfieldMaxSide);
        if (cab.BackglassUrl != null && !cab.HasBackglassArt) _art.Request(cab.BackglassUrl);
        // The table's own VR cabinet, read in the background the first time it's needed.
        if (cab.Table != null && VpxFile(cab.Table) is { } vpx && !_cabs.IsKnown(vpx)) _cabs.Request(vpx);
    }

    /// <summary>The table's .vpx, if it's a VPX table (only those carry a VR cabinet).</summary>
    static string? VpxFile(LobbyTable t) =>
        t.Engine == VrTableManager.Models.TableManifest.Vpx && t.Source.TablePath.EndsWith(".vpx", StringComparison.OrdinalIgnoreCase)
            ? t.Source.TablePath : null;

    // ---- Cabinets: the table's own, else the template, else the drawn one --------------

    /// <summary>
    /// The default cabinet: Medieval Madness's VR cabinet, dumped once from the table (--dump-cabinet) and
    /// shipped with the lobby, so every table without its own wears it from the first frame.
    /// </summary>
    public const string DefaultCabinetFile = "res://cabinet/default.cab";

    RealCabinet? _template;
    bool _borrowAll;

    /// <summary>The cabinet tables without their own VR cabinet borrow (null: the default file is missing, the drawn one is used).</summary>
    public RealCabinet? TemplateCabinet => _template;

    /// <summary>For tests: use this cabinet as the template.</summary>
    public void SetTemplate(RealCabinet? template)
    {
        _template = template;
        foreach (var cab in _pool) if (cab.Table != null) ShowCabinetFor(cab);
    }

    void ShowCabinetFor(Cabinet cab)
    {
        if (cab.Table == null) return;
        var vpx = VpxFile(cab.Table);
        // --borrow-all (diagnostic): every table borrows the template, to preview how it dresses other tables.
        var own = vpx != null && !_borrowAll ? _cabs.Get(vpx) : null;
        if (own != null) cab.ShowReal(own);
        // Not read yet: it may have its own, so show nothing rather than the default cabinet swapping out.
        else if (vpx != null && !_borrowAll && !_cabs.IsKnown(vpx)) cab.ShowLoading();
        else if (_template != null) cab.ShowReal(_template, borrowed: true);
        else cab.ShowReal(null);
    }

    void OnCabinetReady(string vpx, RealCabinet? real)
    {
        foreach (var cab in _pool) ShowCabinetFor(cab);
    }


    /// <summary>
    /// Places the pooled cabinets around the ring. Table i always lands in slot i mod 11, so scrolling
    /// one step re-assigns just one cabinet. Art is only requested once the ring has (nearly) stopped,
    /// so flying through the alphabet doesn't decode pictures nobody sees.
    /// </summary>
    void Layout()
    {
        var centre = (int)MathF.Round(_scroll);
        var settledEnough = Mathf.Abs(_scroll - Selected) < 2.5f;
        for (var i = centre - Side; i <= centre + Side; i++)
        {
            var cab = _pool[((i % PoolSize) + PoolSize) % PoolSize];
            if (i < 0 || i >= Shown.Count)
            {
                cab.Park();
                continue;
            }
            if (cab.Index != i || cab.Table != Shown[i]) AssignCabinet(cab, i, settledEnough);
            else if (settledEnough) RequestArt(cab);

            var offset = i - _scroll;
            var a = offset * Step;
            var focus = Mathf.Clamp(1f - Mathf.Abs(offset), 0f, 1f); // 1 for the selected cabinet
            var front = Mathf.Lerp(RingFront, SelectedFront, focus);
            var dist = front + CabinetKit.Length / 2;
            cab.Position = new Vector3(Mathf.Sin(a) * dist, 0, -Mathf.Cos(a) * dist);
            // Face the player, then turn neighbours a little away so the selected one stands out.
            cab.Rotation = new Vector3(0, -a + Mathf.Sign(offset) * Mathf.Min(Mathf.Abs(offset), 1f) * AngleAway, 0);
            cab.SetFocus(focus, i == Selected);
        }
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
        var spread = Mathf.DegToRad(LetterRail ? 74f : 66f);
        for (var k = 0; k < count; k++)
        {
            var g = first + k;
            var t = count == 1 ? 0.5f : (float)k / (count - 1);
            var a = Mathf.Lerp(-spread, spread, t);
            var label = new Label3D
            {
                Text = Groups[g].Label,
                Font = _displayFont,
                FontSize = LetterRail ? 58 : 34,
                PixelSize = 0.0012f,
                OutlineSize = 10,
                Position = new Vector3(Mathf.Sin(a) * RailR, RailY, -Mathf.Cos(a) * RailR),
                Rotation = new Vector3(RailTilt, -a, 0),
            };
            var area = new Area3D();
            area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(LetterRail ? 0.06f : 0.2f, 0.08f, 0.02f) } });
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
                label.Modulate = Palette.Lamp;   // lit, like a playfield insert
                label.Scale = Vector3.One * 1.35f;
            }
            else if (Groups[g].Index == -1)
            {
                label.Modulate = new Color(Palette.Muted, 0.25f);
                label.Scale = Vector3.One;
            }
            else
            {
                label.Modulate = g == _hoverRail ? Palette.Lamp.Lightened(0.35f) : new Color(Palette.Text, 0.8f);
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

    void ShowFlash(string? text = null)
    {
        if (text == null)
        {
            var g = LobbyNav.GroupAt(Groups, Selected);
            if (g == -1) return;
            text = Groups[g].Label;
        }
        _flash.Text = text;
        _flash.FontSize = text.Length <= 2 ? 260 : 120;
        _flashTime = 0.7;
    }

    // ---- Selection ------------------------------------------------------------------

    void UpdateSelection()
    {
        UpdateRail();
        StopAttract();
        _settled = 0;
        if (Shown.Count == 0)
        {
            _header.Text = "No tables here";
            _status.Text = LobbyText.Empty(State.Filter, Library.Count);
            _status.Modulate = Palette.Text;
            _scores.Text = "";
            _scoresPanel.Visible = false;
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
        _status.Modulate = t.MissingRoms.Count > 0 ? Palette.Red.Lightened(0.25f) : Palette.Text;
        ShowScores(t);
    }

    // ---- High scores ------------------------------------------------------------------

    /// <summary>
    /// Grand Champion and the top four from the table's PinMAME .nv file, under the stats. Read off
    /// the main thread (a map plus up to 128 KB of NVRAM) and remembered per table; nothing is shown
    /// for tables without a ROM, an .nv file or a known memory map.
    /// </summary>
    void ShowScores(LobbyTable t)
    {
        _scores.Text = "";
        _scoresPanel.Visible = false;
        if (t.Engine != VrTableManager.Models.TableManifest.Vpx) return;
        if (_scoreLines.TryGetValue(t.Id, out var lines)) { PlaceScores(lines); return; }
        var id = t.Id;
        Task.Run(() => LobbyText.HighScoreLines(HighScores.Read(t.Source))).ContinueWith(task =>
        {
            if (!IsInstanceValid(this)) return;
            var result = task.IsCompletedSuccessfully ? task.Result : [];
            Callable.From(() =>
            {
                _scoreLines[id] = result;
                if (Shown.Count > 0 && Shown[Selected].Id == id) PlaceScores(result);
            }).CallDeferred();
        });
    }

    void PlaceScores(IReadOnlyList<string> lines)
    {
        _scores.Text = string.Join("\n", lines);
        _scoresPanel.Visible = lines.Count > 0;
        if (lines.Count == 0) return;
        // Just under however many lines the stats text wrapped to, on a dark DMD-glass panel so the
        // lamps on the far wall don't run through the dots.
        var statusSize = (_status.Font ?? ThemeDB.FallbackFont).GetMultilineStringSize(_status.Text, HorizontalAlignment.Center, _status.Width, _status.FontSize);
        _scores.Position = _status.Position + Vector3.Down * (statusSize.Y * _status.PixelSize + 0.035f);
        var size = (_scores.Font ?? ThemeDB.FallbackFont).GetMultilineStringSize(_scores.Text, HorizontalAlignment.Center, -1, _scores.FontSize) * _scores.PixelSize;
        ((QuadMesh)_scoresPanel.Mesh).Size = size + new Vector2(0.06f, 0.025f);
        _scoresPanel.Position = _scores.Position + new Vector3(0, -size.Y / 2, -0.004f);
    }

    /// <summary>Fetches the community NVRAM maps once a month, in the background; re-reads scores if they just arrived.</summary>
    void RefreshHighScoreMaps()
    {
        var had = File.Exists(Path.Combine(HighScores.MapsDir, "index.json"));
        HighScores.EnsureMapsAsync(CancellationToken.None).ContinueWith(task =>
        {
            if (had || !task.IsCompletedSuccessfully || !task.Result || !IsInstanceValid(this)) return;
            Callable.From(() =>
            {
                _scoreLines.Clear();
                if (Shown.Count > 0) ShowScores(Shown[Selected]);
            }).CallDeferred();
        });
    }

    public string ScoresText => _scores.Text;

    public void Select(int i)
    {
        if (Shown.Count == 0) return;
        var before = Selected;
        Selected = Math.Clamp(i, 0, Shown.Count - 1);
        if (Selected == before) return;
        // Long jumps (A → M) start the animation a few cabinets away instead of spinning through dozens.
        if (Mathf.Abs(Selected - _scroll) > Side * 2) _scroll = Selected + Mathf.Sign(_scroll - Selected) * Side;
        UpdateSelection();
        _left?.TriggerHapticPulse("haptic", 0.0, 0.3, 0.05, 0.0);
        _right?.TriggerHapticPulse("haptic", 0.0, 0.3, 0.05, 0.0);
    }

    /// <summary>Picks a different table at random from what's shown (Y on the controller, Ctrl+R or ? on the keyboard).</summary>
    public void SelectRandom()
    {
        if (Shown.Count < 2) return;
        var r = Random.Shared.Next(Shown.Count - 1);
        Select(r >= Selected ? r + 1 : r);
        ShowFlash("?");
    }

    public void CycleFilter()
    {
        var current = Shown.Count > 0 ? Shown[Selected].Id : "";
        State.Filter = LobbyNav.Filters[(Array.IndexOf(LobbyNav.Filters, State.Filter) + 1) % LobbyNav.Filters.Length];
        State.Save();
        Rebuild(current);
        ShowFlash(State.Filter);
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
        foreach (var cab in _pool)
            if (cab.Table != null) cab.SetFavourite(State.IsFavourite(cab.Table.Id));
        if (IsStatsFilter(State.Filter)) Rebuild(Shown.Count > 0 ? Shown[Selected].Id : "");
        else UpdateSelection();
        return true;
    }

    public void ToggleFavourite()
    {
        if (Shown.Count == 0) return;
        var t = Shown[Selected];
        var on = State.ToggleFavourite(t.Id);
        SelectedCabinet?.SetFavourite(on);
        if (State.Filter == "Favourites" && !on)
            Rebuild(Shown[Math.Min(Selected + 1, Shown.Count - 1)].Id);
        else
            UpdateSelection();
    }

    // ---- Attract sound -----------------------------------------------------------------

    /// <summary>
    /// When a cabinet has been selected for a moment, plays a few seconds of a sound file from the
    /// table's own folder (if there is one), quietly, from its backbox. Never downloads anything.
    /// </summary>
    void MaybePlayAttract()
    {
        if (Shown.Count == 0 || AttractPlaying != null) return;
        var t = Shown[Selected];
        if (!_attractFiles.TryGetValue(t.Id, out var file)) _attractFiles[t.Id] = file = LobbyArt.AttractSound(t);
        if (file == null)
        {
            // A ROM table: record its own sound in the background, once; it plays when ready.
            if (_recordRoms) _recorder.Request(t);
            return;
        }
        try
        {
            AudioStream? stream = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".ogg" => AudioStreamOggVorbis.LoadFromFile(file),
                ".mp3" => new AudioStreamMP3 { Data = File.ReadAllBytes(file) },
                ".wav" => AudioStreamWav.LoadFromFile(file),
                _ => null,
            };
            if (stream == null) return;
            _attract.Stream = stream;
            _attract.Position = new Vector3(0, CabinetKit.BackglassCentreY, -(SelectedFront + CabinetKit.Length) + CabinetKit.BoxDepth);
            _attract.VolumeDb = -12f;
            _attract.Play();
            AttractPlaying = file;
            _attractLeft = 10.0;
        }
        catch (Exception e)
        {
            GD.PushWarning($"Attract sound {file}: {e.Message}");
            _attractFiles[t.Id] = null; // unreadable: don't try again
        }
    }

    void StopAttract()
    {
        if (AttractPlaying == null) return;
        _attract.Stop();
        AttractPlaying = null;
    }

    // ---- Input / frame -------------------------------------------------------------

    public override void _Process(double delta)
    {
        var d = (float)delta;
        if (!Mathf.IsEqualApprox(_scroll, Selected))
        {
            _scroll = Mathf.Lerp(_scroll, Selected, 1f - Mathf.Exp(-9f * d));
            if (Mathf.Abs(_scroll - Selected) < 0.01f) _scroll = Selected; // a third of a degree: snap and settle
            Layout();
        }
        else if (Shown.Count > 0 && !_launching)
        {
            _settled += delta;
            if (_settled > 0.8) MaybePlayAttract();
        }
        if (AttractPlaying != null)
        {
            _attractLeft -= delta;
            if (_attractLeft < 1.5) _attract.VolumeDb = Mathf.Lerp(-40f, -12f, Mathf.Clamp((float)_attractLeft / 1.5f, 0f, 1f));
            if (_attractLeft <= 0) { _attract.Stop(); _attractLeft = double.MaxValue; } // played once per selection
        }
        if (_flashTime > 0)
        {
            _flashTime -= delta;
            _flash.Modulate = new Color(Palette.Dmd, Mathf.Clamp((float)_flashTime / 0.3f, 0f, 0.85f));
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

        // Either stick ← → turns the ring (faster while held); ↑ ↓ jumps between rail groups.
        var step = Stick("x", Strongest(lv.X, rv.X), delta);
        if (step != 0)
        {
            Select(Selected + step);
            if (_held["x"] > 0.6) ShowFlash(); // moving fast: show which letter we're passing
        }
        var j = Stick("jump", Strongest(lv.Y, rv.Y), delta);
        if (j != 0)
        {
            Select(LobbyNav.Jump(Groups, Selected, -j)); // stick up = earlier letter
            ShowFlash();
        }

        // Pointing: at a cabinet for a moment selects it; at the rail highlights a group.
        int slot = -1, rail = -1;
        if (_ray.IsColliding() && _ray.GetCollider() is GodotObject hit)
        {
            if (hit.HasMeta("cabinet_slot")) slot = hit.GetMeta("cabinet_slot").AsInt32();
            else if (hit.HasMeta("rail_group")) rail = hit.GetMeta("rail_group").AsInt32();
        }
        if (rail != _hoverRail)
        {
            _hoverRail = rail;
            UpdateRail();
        }
        if (slot != _hoverSlot)
        {
            _hoverSlot = slot;
            _hoverTime = 0;
        }
        else if (slot != -1 && _pool[slot].Index is var idx && idx >= 0 && idx != Selected)
        {
            _hoverTime += delta;
            if (_hoverTime > 0.25) Select(idx);
        }
    }

    /// <summary>-1/0/+1 for a stick axis with a dead zone; repeats while held, faster the longer it's held.</summary>
    int Stick(string channel, float value, double delta)
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
        _cool[channel] = LobbyNav.RepeatInterval(_held[channel]);
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
                if (controller == _right && _hoverSlot != -1 && _pool[_hoverSlot].Index >= 0) Select(_pool[_hoverSlot].Index);
                _ = LaunchAsync();
                break;
            case "by_button" when controller == _left: SelectRandom(); break;   // Y
            case "by_button": CycleFilter(); break;                             // B
            // The left grip is the height "shift" key, so only the right grip favourites.
            case "grip_click" when controller == _right: ToggleFavourite(); break;
            case "primary_click" when controller == _left && _left!.GetFloat("grip") > 0.6f: ResetHeight(); break;
            case "menu_button": GetTree().Quit(); break;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_launching || @event is not InputEventKey { Pressed: true } k) return;
        switch (k.Keycode)
        {
            case Key.Right: Select(Selected + 1); break;
            case Key.Left: Select(Selected - 1); break;
            case Key.Down or Key.Pagedown: Select(LobbyNav.Jump(Groups, Selected, 1)); ShowFlash(); break;
            case Key.Up or Key.Pageup: Select(LobbyNav.Jump(Groups, Selected, -1)); ShowFlash(); break;
            case Key.Enter or Key.KpEnter or Key.Space: _ = LaunchAsync(); break;
            case Key.Escape: GetTree().Quit(); break;
            case Key.Tab: CycleFilter(); break;
            // Plain letters jump through the alphabet, so random is Ctrl+R or "?".
            case Key.R when k.CtrlPressed: SelectRandom(); break;
            case Key.Slash: SelectRandom(); break;
            case Key.F when !k.CtrlPressed: ToggleFavourite(); break;
            case Key.Equal or Key.KpAdd: SetHeight(State.HeightOffset + LobbyHeight.KeyStep); SaveHeight(); break;
            case Key.Minus or Key.KpSubtract: SetHeight(State.HeightOffset - LobbyHeight.KeyStep); SaveHeight(); break;
            case Key.Home: ResetHeight(); break;
            default:
                if (!k.CtrlPressed && k.Unicode is > 0 and < 128 && char.IsLetterOrDigit((char)k.Unicode)) JumpToKey((char)k.Unicode);
                break;
        }
    }

    // ---- Art / launch --------------------------------------------------------------

    void OnArtLoaded(string url, Texture2D tex, Color average)
    {
        foreach (var cab in _pool)
        {
            if (cab.Table == null) continue;
            if (cab.PlayfieldUrl == url) cab.SetPlayfield(tex);
            if (cab.BackglassUrl == url) cab.SetBackglass(tex, average);
        }
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
        StopAttract();
        _recorder.StopAll();
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
        _status.Modulate = Palette.Red.Lightened(0.25f);
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
