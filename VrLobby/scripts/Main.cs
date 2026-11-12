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
    // Ring of cabinets (distances are to the cabinet's front edge, from the player standing at the origin). How big
    // the cabinets are and how many stand in the ring are Setup choices (LobbyLayout, read from the settings at
    // start-up); the defaults are 11 cabinets 34° apart with their fronts 2.1 m away, as the ring always was.
    LobbyRing _layout = LobbyRing.Default;
    int Side => _layout.Side; // cabinets either side of the selected one
    public int PoolSize => _layout.Count; // only this many cabinet nodes ever exist, however big the library
    public float RingFront => (float)_layout.RingFront;
    public float CabinetScale => (float)_layout.Scale;
    float CabinetLength => CabinetKit.Length * CabinetScale;

    /// <summary>
    /// How much higher the selected cabinet's backbox top stands, and how much further back its front is, than at
    /// 100 %: the title, stats and loading lamps that float in front of it move by this, so they never sink into it.
    /// </summary>
    Vector3 AboveCabinet =>
        new(
            0,
            (CabinetScale - 1) * CabinetKit.Height,
            -(CabinetScale - 1) * (CabinetKit.Length - CabinetKit.BoxDepth)
        );
    const float SnapAt = 0.0005f; // the ring eases to within this much of a step, then stops
    const float VideoFrom = 0.98f; // the playfield video starts as soon as the ring turns towards the table (steps)
    const double AttractAfter = 0.5; // seconds on a table before its sound plays
    public const float SelectedFront = (float)LobbyLayout.SelectedFront; // the selected cabinet steps up to about arm's length

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
    readonly Label3D _header = new(),
        _status = new(),
        _hint = new(),
        _flash = new(),
        _scores = new();
    readonly MeshInstance3D _scoresPanel = new(); // dark DMD glass behind the high scores
    readonly Dictionary<string, IReadOnlyList<string>> _scoreLines = []; // per table id; .nv files only change while a table runs (StartPlaying drops that table's lines)
    readonly Dictionary<string, double> _held = new() { ["x"] = 0, ["jump"] = 0 };
    readonly Dictionary<string, double> _cool = new() { ["x"] = 0, ["jump"] = 0 };

    // Cabinet buttons (flippers, magna-saves, Start…), mapped like the player's VPX controls (CabinetButtons).
    CabinetButtons _cabinet = CabinetButtons.None;
    readonly HashSet<CabinetAction> _cabinetHeld = [];
    Vector2 _padStick; // a game controller's left stick (up = +Y)
    readonly AudioStreamPlayer3D _attract = new() { VolumeDb = -12f, UnitSize = 3f };
    readonly Dictionary<string, string?> _attractFiles = [];
    readonly PlayfieldVideo _video = new();
    readonly LoadingScreenVideo _loadingVideo = new(); // the loading screen's big screen (LoadingVideo)

    // Wheel art (WheelArt): the selected game's logo, floating over its title. Shown once its picture has loaded.
    readonly Sprite3D _wheel = new();
    string? _wheelKey;
    const float WheelMaxWidth = 0.75f,
        WheelMaxHeight = 0.28f;

    /// <summary>Folder of the lobby video playing on the selected cabinet, or null.</summary>
    public string? VideoPlaying => _video.Playing;
    public int VideoFramesShown => _video.FramesShown;
    string? _videoCheckedFor; // looked for this selection's video already (once per selection, not every frame)

    // --perf-trace (diagnostic): steps through tables by itself and prints every slow frame with what happened in it.
    bool _perfTrace;
    readonly List<string> _perfEvents = [];
    double _perfClock,
        _perfNextStep = 2.0;
    int _perfSteps,
        _perfStepCount = 8;

    void Trace(string what)
    {
        if (!_perfTrace)
        {
            return;
        }

        _perfEvents.Add(what);
        GD.Print($"  {_perfClock:0.00}s {what}");
    }

    readonly RomRecorder _recorder = new();
    bool _recordRoms;
    Dictionary<string, string>? _backglass;
    Font? _dmdFont,
        _displayFont,
        _bodyFont; // Doto (dot-matrix), Big Shoulders Display, Archivo

    // Height adjustment: the player's rig (XR origin, or the desktop camera's parent) is raised or
    // lowered by State.HeightOffset. Moving the rig rather than the room keeps every layout constant
    // as-is, and recentering (which moves the tracking origin, not this node) never touches it.
    Node3D _rig = null!;
    readonly Label3D _heightLabel = new();
    double _heightShow,
        _statsCheck = 2.0;
    bool _heightDirty;

    bool _vr;

    // The ring scrolls in "ring positions": table i sits at i + _lap × Shown.Count, so stepping past the last table
    // lands on the first one a single step further on (and back), and the cabinets either side of the seam show
    // the other end of the list. _scroll eases towards Target; cabinets are pooled by ring position.
    float _scroll;
    int _lap;

    /// <summary>The selected table's ring position (Selected, plus whole laps of the list).</summary>
    int Target => Selected + _lap * Shown.Count;

    /// <summary>The ring wraps only when the list fills it: fewer tables would show the same table twice.</summary>
    bool RingWraps => Shown.Count >= PoolSize;
    int _hoverSlot = -1,
        _hoverRail = -1;
    Vector3 _aimAtSelect; // where the laser pointed when the selection last changed
    const float AimMoved = 0.07f; // radians (4°): more than hand jitter, so it means "I pointed somewhere else"
    double _hoverTime,
        _flashTime,
        _settled,
        _attractLeft;
    bool _launching;
    string? _settingsPath,
        _statePath;
    XRController3D? _left,
        _right;
    RayCast3D? _ray;
    OpenXRInterface? _xr;

    // Loading screen (StartLoading): after you pick a table the lobby stays in the headset, showing it with a
    // lamp chase, until the table takes the headset over. Without it you'd wait in an empty view.
    LobbyTable? _loading;
    string? _statusFile;
    double _loadingTime,
        _loadingPoll,
        _unfocused;
    readonly Node3D _chase = new();
    readonly List<StandardMaterial3D> _chaseLamps = [];
    const double LoadingGiveUp = 180; // seconds; then the lobby closes anyway, as it used to straight away

    // Playing (StartPlaying): the table has the headset and the lobby waits behind it, everything hidden, so
    // it can take the headset straight back when the table closes instead of starting up again.
    bool _playing;
    double _returning = -1; // seconds since the table closed, until the runtime gives us focus back; -1 when not waiting
    const double ReturnGiveUp = 8; // then the lobby restarts itself, which gets a fresh session
    readonly List<(Node3D Node, bool Was)> _hidden = [];

    // Taking the desktop window's keyboard back (TakeKeyboard): after every table, not just the first, and
    // retried, because Windows turns down a single request from a window in the background.
    readonly FocusRetry _keyboard = new();
    public bool IsPlaying => _playing;
    public bool IsLoading => _loading != null;

    public override void _Ready()
    {
        _dmdFont = LoadFont("res://fonts/Doto-Black.ttf");
        _displayFont = LoadFont("res://fonts/BigShouldersDisplay-Black.ttf");
        _bodyFont = LoadFont("res://fonts/Archivo-SemiBold.ttf");
        SetWindowIcon();
        SetupView();

        var args = UserArgs();
        // --dump-cabinet=<table.vpx>: write that table's VR cabinet as the default cabinet, then quit.
        if (args.GetValueOrDefault("dump-cabinet") is { Length: > 0 } dumpVpx)
        {
            var ok = RealCabinetLoader.DumpDefault(
                dumpVpx,
                ProjectSettings.GlobalizePath(DefaultCabinetFile)
            );
            GD.Print(
                ok ? $"Default cabinet written from {dumpVpx}" : $"No VR cabinet in {dumpVpx}"
            );
            GetTree().Quit(ok ? 0 : 1);
            return;
        }
        // The default cabinet before any table shows, so nothing ever appears in the drawn one first.
        _template = RealCabinetLoader.LoadDefault(
            ProjectSettings.GlobalizePath(DefaultCabinetFile)
        );
        if (_template == null)
        {
            GD.PushWarning(
                $"No default cabinet at {DefaultCabinetFile}: tables without their own use the drawn cabinet."
            );
        }
        // --settings / --state (or VRLOBBY_SETTINGS / VRLOBBY_STATE) point elsewhere; used by the self-test.
        _settingsPath =
            args.GetValueOrDefault("settings")
            ?? NullIfEmpty(System.Environment.GetEnvironmentVariable("VRLOBBY_SETTINGS"));
        _statePath =
            args.GetValueOrDefault("state")
            ?? NullIfEmpty(System.Environment.GetEnvironmentVariable("VRLOBBY_STATE"));
        var selfTest = args.ContainsKey("selftest");
        _perfTrace = args.ContainsKey("perf-trace");
        if (int.TryParse(args.GetValueOrDefault("perf-trace"), out var steps) && steps >= 0)
        {
            _perfStepCount = steps;
        }

        if (selfTest && _settingsPath == null)
        {
            var fx = Tests.LobbyFixture.Create(
                Path.Combine(OS.GetUserDataDir(), "selftest-fixture")
            );
            _settingsPath = fx.Settings;
            _statePath = Path.Combine(fx.Root, "state.json");
            _cabs.CacheDir = Path.Combine(fx.Root, "cabinet-cache"); // a fresh cache each run, not the real one
        }
        Settings = _settingsPath != null ? AppSettings.LoadFrom(_settingsPath) : AppSettings.Load();
        State = LobbyState.Load(_statePath);
        // The room, its lamps and the cabinet pool are built after the settings are read: their size and
        // number decide where the ring stands.
        _layout = LobbyLayout.Ring(Settings.LobbyCabinetCount, Settings.LobbyCabinetScalePercent);
        GD.Print(
            $"Cabinets: {_layout.Count} at {CabinetScale * 100:0}%, ring {_layout.RingFront:0.00} m"
        );
        // Both caches must hold at least every cabinet on show, or loading one forgets another in the ring.
        _cabs.Keep = RealCabinetLoader.KeepFor(_layout.Count);
        _art.KeepTextures = ArtLoader.KeepFor(_layout.Count);
        BuildRoom();
        BuildChase();
        AddChild(_art);
        _art.Loaded += OnArtLoaded;
        AddChild(_cabs);
        _cabs.CabinetReady += OnCabinetReady;
        AddChild(_ring);
        AddChild(_rail);
        AddChild(_attract);
        AddChild(_video);
        AddChild(_loadingVideo);
        _video.FirstFrameShown += () => Trace("video first frame");
        AddChild(_recorder);
        _recorder.Recorded += id => _attractFiles.Remove(id); // look again: it plays if the table is still selected
        for (var s = 0; s < PoolSize; s++)
        {
            var cab = new Cabinet { Scale = Vector3.One * CabinetScale };
            _ring.AddChild(cab);
            cab.Build(s, _displayFont, _dmdFont);
            _pool.Add(cab);
        }
        ApplyHeight(); // the lobby restarts after every table, so this is where the saved height comes back
        Library = LobbyLibrary.Load(Settings);
        try
        {
            _cabinet = CabinetButtons.Load(Settings);
            GD.Print("Cabinet buttons: " + _cabinet.Describe());
            foreach (var id in Input.GetConnectedJoypads())
            {
                LogJoypad(id, true);
            }

            Input.JoyConnectionChanged += (id, connected) => LogJoypad((int)id, connected);
        }
        catch (Exception e)
        {
            GD.Print("Cabinet buttons not read: " + e.Message);
        }
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
        _noDepth = args.ContainsKey("no-depth"); // flat playfield video, for comparing (PlayfieldDepth)
        // --screenshot=<png> [--pitch=<degrees>] (desktop, diagnostic): save one frame once the art is in, then quit.
        if (!_vr && args.GetValueOrDefault("screenshot") is { Length: > 0 } shot)
        {
            if (
                float.TryParse(
                    args.GetValueOrDefault("pitch"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var pitch
                ) && GetViewport().GetCamera3D() is { } cam
            )
            {
                cam.Rotation = new Vector3(Mathf.DegToRad(pitch), 0, 0);
            }
            // --fov=<degrees>: zoom in from the same standing spot (same perspective, closer look).
            if (
                float.TryParse(
                    args.GetValueOrDefault("fov"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var fov
                ) && GetViewport().GetCamera3D() is { } zoomCam
            )
            {
                zoomCam.Fov = fov;
            }
            // --cam-y=<metres>: eye height for the shot (default standing height), e.g. to look a cabinet's front in the face.
            if (
                float.TryParse(
                    args.GetValueOrDefault("cam-y"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var camY
                ) && GetViewport().GetCamera3D() is { } lowCam
            )
            {
                lowCam.Position = new Vector3(lowCam.Position.X, camY, lowCam.Position.Z);
            }

            GetTree().CreateTimer(6).Timeout += () =>
            {
                GetViewport().GetTexture().GetImage().SavePng(shot);
                Leave("screenshot saved to " + shot);
            };
        }
        Rebuild(args.GetValueOrDefault("select", ""));
        LoadBackglassIndex();
        if (!_noDepth)
        {
            WarmPlayfieldDepth();
        }

        if (!selfTest)
        {
            RefreshHighScoreMaps();
        }

        if (selfTest)
        {
            Callable
                .From(() =>
                {
                    _ = SelfTest.RunAsync(this);
                })
                .CallDeferred();
        }
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
        catch
        {
            return null;
        }
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
            if (bytes.Length > 0 && img.LoadPngFromBuffer(bytes) == Error.Ok)
            {
                DisplayServer.SetIcon(img);
            }
        }
        catch
        { /* cosmetic only */
        }
    }

    /// <summary>Backglass pictures come from the manager's cached VPS catalog (no network); ~8 MB, so it's read in the background.</summary>
    void LoadBackglassIndex()
    {
        Task.Run(() =>
            {
                var games = VpsCatalog.LoadCached() ?? [];
                // Also what smart collections need to know a table's type and themes.
                // Entries without an id are skipped, so one bad entry can't cost the backglass pictures too.
                var byId = games
                    .Where(g => !string.IsNullOrEmpty(g.Id))
                    .GroupBy(g => g.Id)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                return (Backglass: LobbyArt.BackglassIndex(games), ById: byId);
            })
            .ContinueWith(t =>
            {
                var index = t.IsCompletedSuccessfully
                    ? t.Result.Backglass
                    : new Dictionary<string, string>();
                Callable
                    .From(() =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            State.Catalog = t.Result.ById;
                            if (State.IsSmart(State.Filter))
                            {
                                Rebuild(Shown.Count > 0 ? Shown[Selected].Id : "");
                            }
                        }
                        _backglass = index;
                        foreach (var cab in _pool)
                        {
                            if (cab.Table != null)
                            {
                                AssignCabinet(cab, cab.Index, requestArt: true);
                            }
                        }
                    })
                    .CallDeferred();
            });
    }

    // ---- Setup -------------------------------------------------------------------

    void SetupView()
    {
        var xr = XRServer.FindInterface("OpenXR") as OpenXRInterface;
        _vr = xr != null && xr.IsInitialized();
        if (_vr)
        {
            _xr = xr;
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); // the headset paces frames
            // Cabinet boards that act as a keyboard (PinOne, Pinscape…) only reach the focused window, and the
            // lobby is started again in the background after every table.
            TakeKeyboard();
            xr!.SessionStopping += () =>
                Leave(
                    "the headset ended the VR session (Quest Link disconnected, or the app was closed from the Quest menu)"
                );
            // Holding the Quest's Oculus button recenters, and so does VPX's own recenter control (OnCabinet).
            xr.PoseRecentered += Recenter;

            var origin = new XROrigin3D();
            AddChild(origin);
            _rig = origin;
            var cam = new XRCamera3D();
            origin.AddChild(cam);
            AttachHeightLabel(cam);
            _left = MakeController(origin, "left_hand");
            _right = MakeController(origin, "right_hand");
            _ray = new RayCast3D
            {
                TargetPosition = new Vector3(0, 0, -6),
                CollideWithAreas = true,
                CollideWithBodies = false,
            };
            _right.AddChild(_ray);
            _right.AddChild(
                new MeshInstance3D
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
                }
            );
        }
        else
        {
            // Desktop: roughly where you would stand, tilted down a little so the cabinet, the title above it and the rail all fit.
            _rig = new Node3D();
            AddChild(_rig);
            var cam = new Camera3D
            {
                Position = new Vector3(0, 1.62f, 0.3f),
                Rotation = new Vector3(Mathf.DegToRad(-15), 0, 0),
                Fov = 80,
            };
            _rig.AddChild(cam);
            AttachHeightLabel(cam);
        }
    }

    /// <summary>
    /// Puts you where the lobby expects you, facing the selected cabinet, from wherever you stand and look now.
    /// keep_height = true leaves the floor where the headset put it (project.godot asks for the Local Floor
    /// reference space), and the user's height offset lives on the rig node, so recentering can't wipe it.
    /// </summary>
    void Recenter()
    {
        if (!_vr)
        {
            return;
        }

        XRServer.CenterOnHmd(XRServer.RotationMode.ResetButKeepTilt, true);
        Trace("recentered");
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
        Fade(_heightLabel, Colors.White, Colors.Black, 0);
        cam.AddChild(_heightLabel);
    }

    XRController3D MakeController(XROrigin3D origin, string tracker)
    {
        // Aim pose follows where the controller points, so the laser feels natural.
        var c = new XRController3D { Tracker = tracker, Pose = "aim" };
        origin.AddChild(c);
        c.AddChild(
            new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.035f, 0.035f, 0.12f) } }
        );
        c.ButtonPressed += name => OnButton(name, c);
        return c;
    }

    void BuildRoom()
    {
        AddChild(
            new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = Palette.Bg,
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.24f, 0.24f, 0.26f),
                    // The background stays flat black, but reflections come from a small generated map of
                    // the room, so chrome (lockbar, rails, speaker rings) has something to mirror.
                    Sky = new Sky
                    {
                        SkyMaterial = new PanoramaSkyMaterial { Panorama = ReflectionSky() },
                        RadianceSize = Sky.RadianceSizeEnum.Size128,
                    },
                    ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
                    // AgX rolls highlights off instead of clipping them: a white cabinet front under the spotlight
                    // keeps its print rather than turning into a flat orange-white block.
                    TonemapMode = Godot.Environment.ToneMapper.Agx,
                    TonemapExposure = 1.25f,
                    GlowEnabled = true,
                    GlowIntensity = 0.7f,
                    // Only light sources bloom: at the default 1.0, a white lockbar or rail under the spotlight haloed too.
                    GlowHdrThreshold = 1.5f,
                    // Distant cabinets and walls fade into the dark instead of ending at a hard edge.
                    FogEnabled = true,
                    FogLightColor = Palette.Bg,
                    FogDensity = 0.03f,
                    FogSkyAffect = 0f,
                },
            }
        );

        // Arcade carpet underfoot and a black drop ceiling with neon tubes overhead (ArcadeRoom); the far wall
        // stays neutral dark grey, so the lamps, the neon and the DMDs are the light in the room.
        AddChild(ArcadeRoom.Floor(15));
        AddChild(ArcadeRoom.Ceiling(7.2f, RingFront));
        AddChild(
            new MeshInstance3D
            {
                Mesh = new CylinderMesh
                {
                    TopRadius = 7,
                    BottomRadius = 7,
                    Height = 4.5f,
                    RadialSegments = 48,
                    CapTop = false,
                    CapBottom = false,
                },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.05f, 0.05f, 0.055f),
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    Roughness = 0.9f,
                },
                Position = new Vector3(0, 2.25f, 0),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }
        );

        // Trim: rows of small incandescent lamps (like a marquee or a playfield's GI string) and chrome
        // edges, kept dim so the cabinets stay the brightest things in the room. Each row is one draw call.
        var ringOut = RingFront + CabinetLength + 0.35f;
        AddChild(LampRow(ringOut, 0.02f, 72, 0.018f, 1.2f)); // on the floor behind the cabinets
        AddChild(ChromeRing(ringOut + 0.06f, 0.006f, 0.008f)); // with a chrome edge
        AddChild(ChromeRing(0.5f, 0.004f, 0.006f)); // a chrome circle round your feet
        AddChild(LampRow(6.9f, 3.0f, 120, 0.025f, 0.7f)); // one row high on the far wall

        // A warm spotlight on the selected cabinet (the only shadow-casting light), and a neutral fill. It's the only
        // thing that makes the selected cabinet brighter: a table brightens as it swings into the pool of light and
        // fades as it leaves. Narrow, and with a soft edge (attenuation over 1 falls off gradually towards the rim),
        // so the pool covers one cabinet and fades out on the floor around it rather than ending in a hard ring.
        var spot = new SpotLight3D
        {
            SpotAngle = 24,
            SpotRange = 7,
            LightEnergy = 9,
            LightColor = Colors.White.Lerp(Palette.Lamp, 0.3f),
            ShadowEnabled = true,
            SpotAngleAttenuation = 2.2f,
            // Godot's default offsets (3 cm, plus a normal offset) let a strip of light through just under a
            // lockdown bar onto the cabinet front, which should sit in the bar's shadow.
            ShadowBias = 0.004f,
            ShadowNormalBias = 0.3f,
        };
        AddChild(spot);
        var cabCentre = -(SelectedFront + CabinetLength / 2);
        // High and a little in front of the cabinet, like a ceiling can light over your shoulder: from there it reaches
        // the playfield, the backglass and the cabinet's front. (A short-range key light just above the lockdown bar
        // used to help it, and blew the bar out: it was half a metre away, the rest of the cabinet two.)
        spot.LookAtFromPosition(
            new Vector3(0, 3.6f, cabCentre + 1.1f),
            new Vector3(0, 0.85f * CabinetScale, cabCentre),
            Vector3.Up
        );
        // Fill from behind you, so cabinet fronts and sides around the ring read; no shadows (one shadowed light is enough).
        AddChild(
            new OmniLight3D
            {
                Position = new Vector3(0, 2.6f, 0.6f),
                OmniRange = 8,
                LightEnergy = 1.2f,
                LightColor = new Color(0.92f, 0.92f, 0.95f),
            }
        );

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
        _header.Position = new Vector3(0, 2.34f, -1.8f) + AboveCabinet;
        // The game's logo floats over the title, unlit (like the lit display text) and keeping its transparency.
        _wheel.Shaded = false;
        _wheel.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
        _wheel.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _wheel.Visible = false;
        AddChild(_wheel);
        _status.Font = _bodyFont;
        _status.FontSize = 38;
        _status.VerticalAlignment = VerticalAlignment.Top;
        _status.Position = new Vector3(0, 2.29f, -1.8f) + AboveCabinet;
        _status.Modulate = Palette.Text;
        // High scores read from the table's PinMAME .nv file, in DMD orange dot-matrix under the stats; placed by ShowScores.
        _scores.Font = _dmdFont;
        _scores.FontSize = 32;
        _scores.Modulate = Palette.Dmd;
        _scores.OutlineModulate = Palette.DmdPanel;
        _scores.VerticalAlignment = VerticalAlignment.Top;
        _scores.Position = _status.Position;
        _scoresPanel.Mesh = new QuadMesh();
        _scoresPanel.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = Palette.DmdPanel,
        };
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
                + "Height: hold left grip + left stick ↑ ↓  ·  left grip + click left stick: reset\n"
                + "Cabinet: flippers browse  ·  magna-saves jump A–Z  ·  Start or Launch: play  ·  Coin: filter  ·  Extra ball: random  ·  hold Exit: quit"
            : "← → browse  ·  ↑ ↓ / PgUp PgDn / type a letter: jump  ·  Enter: play  ·  Ctrl+R or ?: random  ·  Tab: filter  ·  F: favourite  ·  Esc: quit\n"
                + "Height: + / −  ·  Home: reset";
        _flash.Font = _dmdFont;
        _flash.FontSize = 260;
        _flash.OutlineSize = 40;
        _flash.OutlineModulate = Palette.DmdPanel;
        _flash.NoDepthTest = true;
        _flash.RenderPriority = 10;
        _flash.Position = new Vector3(0, 1.45f, -1.25f);
        Fade(_flash, Palette.Dmd, Palette.DmdPanel, 0);
    }

    /// <summary>
    /// What the chrome reflects (an equirectangular map, top row = straight up): a dark room, a soft
    /// bright ceiling light, and a warm band of lamps near the horizon. Half-float, so the light can be
    /// brighter than white and give chrome its sharp highlights.
    /// </summary>
    static ImageTexture ReflectionSky()
    {
        const int w = 128,
            h = 64;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgbah);
        for (var y = 0; y < h; y++)
        {
            var v = (y + 0.5f) / h; // 0 = up, 0.5 = horizon, 1 = down
            var c = new Color(0.035f, 0.035f, 0.04f); // the room
            c +=
                new Color(1f, 0.98f, 0.95f)
                * (3.2f * Mathf.Exp(-Mathf.Pow((v - 0.16f) / 0.06f, 2))); // ceiling light
            c += Palette.Lamp * (0.8f * Mathf.Exp(-Mathf.Pow((v - 0.45f) / 0.015f, 2))); // lamps round the room
            if (v > 0.5f)
            {
                c *= 0.35f; // dark floor
            }

            for (var x = 0; x < w; x++)
            {
                img.SetPixel(x, y, c);
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>A circle of small lit lamp bulbs (one MultiMesh, so one draw call).</summary>
    static MultiMeshInstance3D LampRow(float radius, float y, int count, float bulb, float energy)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new SphereMesh
            {
                Radius = bulb,
                Height = bulb * 2,
                RadialSegments = 8,
                Rings = 4,
            },
            InstanceCount = count,
        };
        for (var i = 0; i < count; i++)
        {
            var a = Mathf.Tau * i / count;
            mm.SetInstanceTransform(
                i,
                new Transform3D(
                    Basis.Identity,
                    new Vector3(Mathf.Sin(a) * radius, y, -Mathf.Cos(a) * radius)
                )
            );
        }
        return new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = Palette.Lamp,
                EmissionEnabled = true,
                Emission = Palette.Lamp,
                EmissionEnergyMultiplier = energy,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    static MeshInstance3D ChromeRing(float radius, float y, float thickness) =>
        new()
        {
            Mesh = new TorusMesh
            {
                InnerRadius = radius - thickness,
                OuterRadius = radius + thickness,
                Rings = 96,
                RingSegments = 8,
            },
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
        if (!_heightDirty)
        {
            return;
        }

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
        Selected = Math.Max(
            0,
            Shown.FindIndex(t => string.Equals(t.Id, selectId, StringComparison.OrdinalIgnoreCase))
        );
        _lap = 0;
        _scroll = Target;
        foreach (var cab in _pool)
        {
            cab.Park(); // the list changed: every cabinet gets re-assigned
        }

        BuildRail();
        UpdateSelection();
        Layout();
    }

    /// <summary>Selected cabinet's slot, or null when the list is empty.</summary>
    public Cabinet? SelectedCabinet =>
        _pool.FirstOrDefault(c => c.Index == Selected && c.Table != null);

    void AssignCabinet(Cabinet cab, int index, bool requestArt)
    {
        var t = Shown[index];
        cab.Assign(
            index,
            t,
            LobbyArt.PlayfieldSource(t),
            LobbyArt.BackglassFor(t, _backglass),
            State.IsFavourite(t.Id)
        );
        // Already read this table's own cabinet (or know it has none)? Show it straight away.
        ShowCabinetFor(cab);
        if (requestArt)
        {
            RequestArt(cab);
        }
    }

    void RequestArt(Cabinet cab)
    {
        // Playfield: the table file's own picture at up to 2048 px, else the VPS picture.
        if (cab.PlayfieldUrl != null && !cab.HasPlayfieldArt && cab.Table != null)
        {
            _art.Request(
                cab.PlayfieldUrl,
                LobbyArt.PlayfieldUrl(cab.Table),
                ArtLoader.PlayfieldMaxSide
            );
        }

        if (cab.BackglassUrl != null && !cab.HasBackglassArt)
        {
            _art.Request(cab.BackglassUrl);
        }
        // The table's own VR cabinet, read in the background the first time it's needed.
        if (cab.Table != null && VpxFile(cab.Table) is { } vpx)
        {
            if (_cabs.IsKnown(vpx))
            {
                _cabs.Touch(vpx); // on show: the newest, so reading the next table forgets one that isn't
            }
            else
            {
                _cabs.Request(vpx);
            }
        }
    }

    /// <summary>The table's .vpx, if it's a VPX table (only those carry a VR cabinet).</summary>
    static string? VpxFile(LobbyTable t) =>
        t.Engine == VrTableManager.Models.TableManifest.Vpx
        && t.Source.TablePath.EndsWith(".vpx", StringComparison.OrdinalIgnoreCase)
            ? t.Source.TablePath
            : null;

    // ---- Cabinets: the table's own, else the template, else the drawn one --------------

    /// <summary>
    /// The default cabinet: Medieval Madness's VR cabinet, dumped once from the table (--dump-cabinet) and
    /// shipped with the lobby, so every table without its own wears it from the first frame.
    /// </summary>
    public const string DefaultCabinetFile = "res://cabinet/default.cab";

    RealCabinet? _template;
    bool _borrowAll,
        _noDepth;

    /// <summary>The cabinet tables without their own VR cabinet borrow (null: the default file is missing, the drawn one is used).</summary>
    public RealCabinet? TemplateCabinet => _template;

    /// <summary>For tests: use this cabinet as the template.</summary>
    public void SetTemplate(RealCabinet? template)
    {
        _template = template;
        foreach (var cab in _pool)
        {
            if (cab.Table != null)
            {
                ShowCabinetFor(cab);
            }
        }
    }

    void ShowCabinetFor(Cabinet cab)
    {
        if (cab.Table == null)
        {
            return;
        }

        var vpx = VpxFile(cab.Table);
        // --borrow-all (diagnostic): every table borrows the template, to preview how it dresses other tables.
        var own = vpx != null && !_borrowAll ? _cabs.Get(vpx) : null;
        if (own != null)
        {
            cab.ShowReal(own);
        }
        // Not read yet: it may have its own, so show nothing rather than the default cabinet swapping out.
        else if (vpx != null && !_borrowAll && !_cabs.IsKnown(vpx))
        {
            cab.ShowLoading();
        }
        else if (_template != null)
        {
            cab.ShowReal(_template, borrowed: true);
        }
        else
        {
            cab.ShowReal(null);
        }
    }

    void OnCabinetReady(string vpx, RealCabinet? real)
    {
        Trace("cabinet " + Path.GetFileNameWithoutExtension(vpx));
        foreach (var cab in _pool)
        {
            ShowCabinetFor(cab);
        }
    }

    /// <summary>
    /// Places the pooled cabinets around the ring. Ring position i always lands in slot i mod 11, so
    /// scrolling one step re-assigns just one cabinet, across the end-to-start seam too. Art is only
    /// requested once the ring has (nearly) stopped, so flying through the alphabet doesn't decode
    /// pictures nobody sees.
    /// </summary>
    void Layout()
    {
        var n = Shown.Count;
        var centre = (int)MathF.Round(_scroll);
        var settledEnough = Mathf.Abs(_scroll - Target) < 2.5f;
        for (var i = centre - Side; i <= centre + Side; i++)
        {
            var cab = _pool[((i % PoolSize) + PoolSize) % PoolSize];
            // Which table stands at ring position i: past either end of the list, the other end's.
            var t =
                n == 0 ? -1
                : RingWraps ? ((i % n) + n) % n
                : i - _lap * n;
            if (t < 0 || t >= n)
            {
                cab.Park();
                continue;
            }
            if (cab.Index != t || cab.Table != Shown[t])
            {
                AssignCabinet(cab, t, settledEnough);
            }
            else if (settledEnough)
            {
                RequestArt(cab);
            }

            // Where on the ring (LobbyLayout.Place, shared with SmokeTest's overlap checks): facing you, neighbours
            // turned a little away so the selected one stands out, which steps up to arm's length.
            var spot = LobbyLayout.Place(_layout, i - _scroll);
            cab.Position = new Vector3((float)spot.X, 0, (float)spot.Z);
            cab.Rotation = new Vector3(0, (float)spot.Yaw, 0);
            cab.SetFocus((float)spot.Focus, i == Target);
        }
    }

    // ---- Jump rail -------------------------------------------------------------------

    bool LetterRail => Groups.Count == LobbyNav.Letters.Length;

    void BuildRail()
    {
        foreach (var (label, _) in _railItems)
        {
            label.QueueFree();
        }

        _railItems.Clear();
        if (Groups.Count == 0)
        {
            return;
        }

        var current = LobbyNav.GroupAt(Groups, Selected);
        // Letters all fit; long lists (manufacturers) show a window around the current one.
        int first = 0,
            count = Groups.Count;
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
            area.AddChild(
                new CollisionShape3D
                {
                    Shape = new BoxShape3D
                    {
                        Size = new Vector3(LetterRail ? 0.06f : 0.2f, 0.08f, 0.02f),
                    },
                }
            );
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
            if (
                current < firstShown
                || current > lastShown
                || (current == firstShown && current > 0)
                || (current == lastShown && current < Groups.Count - 1)
            )
            {
                Callable.From(BuildRail).CallDeferred();
                return;
            }
        }
        foreach (var (label, g) in _railItems)
        {
            if (g == current)
            {
                label.Modulate = Palette.Lamp; // lit, like a playfield insert
                label.Scale = Vector3.One * 1.35f;
            }
            else if (Groups[g].Index == -1)
            {
                label.Modulate = new Color(Palette.Muted, 0.25f);
                label.Scale = Vector3.One;
            }
            else
            {
                label.Modulate =
                    g == _hoverRail ? Palette.Lamp.Lightened(0.35f) : new Color(Palette.Text, 0.8f);
                label.Scale = Vector3.One * (g == _hoverRail ? 1.2f : 1f);
            }
        }
    }

    public void JumpToGroup(int g)
    {
        if (g < 0 || g >= Groups.Count)
        {
            return;
        }

        Select(LobbyNav.IndexForGroup(Groups, g));
        ShowFlash();
    }

    public void JumpToKey(char key) => JumpToGroup(LobbyNav.GroupForKey(Groups, key));

    /// <summary>Closes the lobby, saying why in its log (godot*.log), so an unexpected close can be traced.</summary>
    void Leave(string why)
    {
        GD.Print($"Lobby closing: {why}");
        // Background work using Godot's Image (art, cabinets, height maps, video frames) stops first: the engine
        // shutting down under a worker still inside an Image call crashed the lobby on its way out.
        LobbyWork.StopAndWait(1500);
        GetTree().Quit();
    }

    double _menuHeld = -1; // seconds the controller menu button has been held, or -1

    /// <summary>Holding the menu button for a second leaves the lobby; letting go sooner cancels.</summary>
    void TrackMenuHold(double delta)
    {
        if (_menuHeld < 0)
        {
            return;
        }

        if (
            _left?.IsButtonPressed("menu_button") != true
            && _right?.IsButtonPressed("menu_button") != true
            && !_cabinetHeld.Contains(CabinetAction.Leave)
        )
        {
            _menuHeld = -1;
            return;
        }
        _menuHeld += delta;
        if (_menuHeld >= 1.0)
        {
            _menuHeld = -1;
            Leave("menu button held");
        }
        else
        {
            ShowFlash("HOLD TO LEAVE");
        }
    }

    /// <summary>
    /// Fades a label in or out, outline too: a Label3D's outline has its own colour that Modulate doesn't touch, so
    /// fading only the text left its dark outline hanging in the air as black text. Hidden once fully faded.
    /// </summary>
    static void Fade(Label3D label, Color text, Color outline, float alpha)
    {
        label.Modulate = new Color(text, alpha);
        label.OutlineModulate = new Color(outline, alpha);
        label.Visible = alpha > 0.001f;
    }

    /// <summary>
    /// The filter you just switched to, big in front of you. It stays longer than other flashes, since a name like
    /// "Forgotten favourites" takes a moment to read.
    /// </summary>
    void ShowFilterFlash()
    {
        ShowFlash(State.Filter);
        _flashTime = NameFlashSeconds;
        _flashFade = 0.6;
    }

    /// <summary>
    /// A quick flash: the rail's letter or group as you move along it, "?" for a random table, HOLD TO LEAVE. It
    /// changes quickly (and is shown again every frame while held), so it goes quickly.
    /// </summary>
    void ShowFlash(string? text = null)
    {
        if (text == null)
        {
            var g = LobbyNav.GroupAt(Groups, Selected);
            if (g == -1)
            {
                return;
            }

            text = Groups[g].Label;
        }
        _flash.Text = text;
        _flash.FontSize = text.Length <= 2 ? 260 : 120;
        _flashTime = LetterFlashSeconds;
        _flashFade = 0.3;
    }

    public const double LetterFlashSeconds = 0.7,
        NameFlashSeconds = 2.2;

    /// <summary>How long the flash takes to fade out at the end of its time.</summary>
    double _flashFade = 0.3;

    /// <summary>Seconds the flash has left (for the self-test).</summary>
    public double FlashTimeLeft => _flashTime;

    // ---- Selection ------------------------------------------------------------------

    void UpdateSelection()
    {
        UpdateRail();
        StopAttract();
        _video.Stop();
        _videoCheckedFor = null;
        _settled = 0;
        _wheel.Visible = false;
        _wheelKey = null;
        _wheelCheckedFor = null;
        if (Shown.Count == 0)
        {
            _header.Text = "No tables here";
            _status.Text = LobbyText.Empty(
                State.Filter,
                Library.Count,
                State.IsCollection(State.Filter),
                State.IsSmart(State.Filter)
            );
            _status.Modulate = Palette.Text;
            _scores.Text = "";
            _scoresPanel.Visible = false;
            return;
        }
        var t = Shown[Selected];
        _header.Text = (State.IsFavourite(t.Id) ? "★ " : "") + t.Title;
        var where = $"{Selected + 1} / {Shown.Count}  ·  {State.Filter}";
        var g = LobbyNav.GroupAt(Groups, Selected);
        if (g != -1 && State.Filter is "Manufacturer" or "Decade")
        {
            where += ": " + Groups[g].Label;
        }
        // Shared with the manager: plays and time are counted by VrTableManager --play.
        var lines = new List<string>
        {
            t.Subtitle,
            LobbyText.Stats(State.Stats, t.Id, DateTime.Now),
            where,
        };
        if (t.MissingRoms.Count > 0)
        {
            lines.Add(
                "Needs ROM: "
                    + string.Join(", ", t.MissingRoms)
                    + ".zip (add it in VR Table Manager)"
            );
        }

        _status.Text = string.Join("\n", lines);
        _status.Modulate = t.MissingRoms.Count > 0 ? Palette.Red.Lightened(0.25f) : Palette.Text;
        // An error about this table (it failed to start) stays until you move on, through stats refreshes.
        if (_error is { } e && e.TableId == t.Id)
        {
            _status.Text = e.Message;
            _status.Modulate = Palette.Red.Lightened(0.25f);
        }
        else
        {
            _error = null;
        }

        ShowScores(t);
    }

    // ---- Wheel art ------------------------------------------------------------------

    /// <summary>Each table's wheel art key (null: none), looked up once per lobby run: no folder reads while scrolling.</summary>
    readonly Dictionary<string, string?> _wheelKeys = [];
    string? _wheelCheckedFor;

    /// <summary>Looks each table's wheel art up again (it was added or removed in the manager).</summary>
    public void ForgetWheelLookups()
    {
        _wheelKeys.Clear();
        _wheelCheckedFor = null;
    }

    /// <summary>
    /// Once the ring has (nearly) brought a table in, as for its playfield video, asks for its logo;
    /// <see cref="ShowWheel"/> puts it up when it's loaded. Tables flown past are never looked up or decoded.
    /// </summary>
    void MaybeRequestWheel()
    {
        if (Shown.Count == 0 || Shown[Selected] is not { } t || _wheelCheckedFor == t.Id)
        {
            return;
        }

        _wheelCheckedFor = t.Id;
        if (!_wheelKeys.TryGetValue(t.Id, out var key))
        {
            try
            {
                key = LobbyArt.WheelFor(t);
            }
            catch (Exception e)
            {
                GD.PushWarning($"Wheel art for {t.Title}: {e.Message}");
                key = null;
            }
            _wheelKeys[t.Id] = key;
        }
        _wheelKey = key;
        if (_wheelKey != null)
        {
            _art.Request(_wheelKey, maxSide: ArtLoader.MaxSide, keepAlpha: true);
        }
    }

    void ShowWheel(Texture2D tex)
    {
        int w = tex.GetWidth(),
            h = tex.GetHeight();
        if (w <= 0 || h <= 0)
        {
            return;
        }

        _wheel.Texture = tex;
        _wheel.PixelSize = Math.Min(WheelMaxWidth / w, WheelMaxHeight / h);
        PlaceWheel();
        _wheel.Visible = true;
    }

    /// <summary>Just over the title, however many lines it wrapped to.</summary>
    void PlaceWheel()
    {
        if (_wheel.Texture is not { } tex)
        {
            return;
        }

        var title = (_header.Font ?? ThemeDB.FallbackFont).GetMultilineStringSize(
            _header.Text,
            HorizontalAlignment.Center,
            _header.Width,
            _header.FontSize
        );
        var height = tex.GetHeight() * _wheel.PixelSize;
        _wheel.Position =
            _header.Position + Vector3.Up * (title.Y * _header.PixelSize + 0.04f + height / 2);
    }

    public bool WheelShowing => _wheel.Visible;
    public string? WheelKey => _wheelKey;
    public bool WheelTransparent =>
        _wheel.Texture?.GetImage() is { } img && img.DetectAlpha() != Image.AlphaMode.None;
    public bool WheelAboveTitle =>
        _wheel.Position.Y - _wheel.Texture!.GetHeight() * _wheel.PixelSize / 2 > _header.Position.Y;

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
        if (t.Engine != VrTableManager.Models.TableManifest.Vpx)
        {
            return;
        }

        if (_scoreLines.TryGetValue(t.Id, out var lines))
        {
            PlaceScores(lines);
            return;
        }
        var id = t.Id;
        Task.Run(() => LobbyText.HighScoreLines(HighScores.Read(t.Source)))
            .ContinueWith(task =>
            {
                if (!IsInstanceValid(this))
                {
                    return;
                }

                var result = task.IsCompletedSuccessfully ? task.Result : [];
                Callable
                    .From(() =>
                    {
                        _scoreLines[id] = result;
                        if (Shown.Count > 0 && Shown[Selected].Id == id)
                        {
                            PlaceScores(result);
                        }
                    })
                    .CallDeferred();
            });
    }

    void PlaceScores(IReadOnlyList<string> lines)
    {
        _scores.Text = string.Join("\n", lines);
        _scoresPanel.Visible = lines.Count > 0;
        if (lines.Count == 0)
        {
            return;
        }
        // Just under however many lines the stats text wrapped to, on a dark DMD-glass panel so the
        // lamps on the far wall don't run through the dots.
        var statusSize = (_status.Font ?? ThemeDB.FallbackFont).GetMultilineStringSize(
            _status.Text,
            HorizontalAlignment.Center,
            _status.Width,
            _status.FontSize
        );
        _scores.Position =
            _status.Position + Vector3.Down * (statusSize.Y * _status.PixelSize + 0.035f);
        var size =
            (_scores.Font ?? ThemeDB.FallbackFont).GetMultilineStringSize(
                _scores.Text,
                HorizontalAlignment.Center,
                -1,
                _scores.FontSize
            ) * _scores.PixelSize;
        ((QuadMesh)_scoresPanel.Mesh).Size = size + new Vector2(0.06f, 0.025f);
        _scoresPanel.Position = _scores.Position + new Vector3(0, -size.Y / 2, -0.004f);
    }

    /// <summary>Fetches the community NVRAM maps once a month, in the background; re-reads scores if they just arrived.</summary>
    void RefreshHighScoreMaps()
    {
        var had = File.Exists(Path.Combine(HighScores.MapsDir, "index.json"));
        HighScores
            .EnsureMapsAsync(CancellationToken.None)
            .ContinueWith(task =>
            {
                if (had || !task.IsCompletedSuccessfully || !task.Result || !IsInstanceValid(this))
                {
                    return;
                }

                Callable
                    .From(() =>
                    {
                        _scoreLines.Clear();
                        if (Shown.Count > 0)
                        {
                            ShowScores(Shown[Selected]);
                        }
                    })
                    .CallDeferred();
            });
    }

    public string ScoresText => _scores.Text;

    /// <summary>
    /// Moves the selection by steps (← →, cabinet buttons): past the last table comes the first, and before the
    /// first the last, one step away like any other.
    /// </summary>
    public void Step(int steps)
    {
        var n = Shown.Count;
        if (n < 2 || steps == 0)
        {
            return;
        }

        var to = Target + steps;
        var t = ((to % n) + n) % n;
        if (RingWraps)
        {
            Select(t, lap: (to - t) / n); // stays on the ring position it stepped to, across the seam
        }
        else
        {
            // Too few to go round: the other end slides in from the side we're heading.
            var wrapped = to - _lap * n != t;
            Select(t);
            if (wrapped)
            {
                _scroll = Target - Math.Sign(steps);
                Layout();
            }
        }
    }

    /// <summary>Selects table i (clicked, typed, jumped to), approaching it the short way round the ring.</summary>
    public void Select(int i) => Select(i, lap: null);

    void Select(int i, int? lap)
    {
        if (Shown.Count == 0)
        {
            return;
        }

        var before = Target;
        Selected = Math.Clamp(i, 0, Shown.Count - 1);
        if (RingWraps)
        {
            // Unless stepping says which lap: the copy of it nearest where the ring is now, so clicking a cabinet
            // across the seam, or Z → A, is a short turn.
            _lap = lap ?? (int)MathF.Round((_scroll - Selected) / Shown.Count);
        }
        if (Target == before)
        {
            return;
        }
        // Long jumps (A → M) start the animation a few cabinets away instead of spinning through dozens.
        if (Mathf.Abs(Target - _scroll) > Side * 2)
        {
            _scroll = Target + Mathf.Sign(_scroll - Target) * Side;
        }

        UpdateSelection();
        if (_ray != null)
        {
            _aimAtSelect = -_ray.GlobalBasis.Z;
        }
        _left?.TriggerHapticPulse("haptic", 0.0, 0.3, 0.05, 0.0);
        _right?.TriggerHapticPulse("haptic", 0.0, 0.3, 0.05, 0.0);
    }

    /// <summary>Picks a different table at random from what's shown (Y on the controller, Ctrl+R or ? on the keyboard).</summary>
    public void SelectRandom()
    {
        if (Shown.Count < 2)
        {
            return;
        }

        var r = Random.Shared.Next(Shown.Count - 1);
        Select(r >= Selected ? r + 1 : r);
        ShowFlash("?");
    }

    public void CycleFilter()
    {
        var current = Shown.Count > 0 ? Shown[Selected].Id : "";
        // The built-in filters, then your collections and smart ones from the manager.
        State.RefreshCollections();
        State.Filter = LobbyNav.NextFilter(State, Library);
        State.Save();
        Rebuild(current);
        ShowFilterFlash();
    }

    public string HeaderText => _header.Text;
    public string StatusText => _status.Text;

    /// <summary>Filters whose tables depend on favourites and plays (smart collections can too).</summary>
    bool IsStatsFilter(string filter) =>
        filter is "Favourites" or "Recently played" or "Most played" || State.IsSmart(filter);

    /// <summary>
    /// Picks up favourites / plays changed by the manager (or the --play launcher) while the lobby is open.
    /// Returns true when the shared stats file had changed.
    /// </summary>
    public bool RefreshFromStats()
    {
        // Collections changed in the manager: the list changes when one is showing, or it's gone.
        if (State.RefreshCollections())
        {
            var gone = State.KeepFilterValid();
            if (gone)
            {
                State.Save();
                ShowFilterFlash();
            }
            if (gone || State.IsCollection(State.Filter) || State.IsSmart(State.Filter))
            {
                State.RefreshStats();
                Rebuild(Shown.Count > 0 ? Shown[Selected].Id : "");
                return true;
            }
        }

        if (!State.RefreshStats())
        {
            return false;
        }

        foreach (var cab in _pool)
        {
            if (cab.Table != null)
            {
                cab.SetFavourite(State.IsFavourite(cab.Table.Id));
            }
        }

        if (IsStatsFilter(State.Filter))
        {
            Rebuild(Shown.Count > 0 ? Shown[Selected].Id : "");
        }
        else
        {
            UpdateSelection();
        }

        return true;
    }

    public void ToggleFavourite()
    {
        if (Shown.Count == 0)
        {
            return;
        }

        var t = Shown[Selected];
        var on = State.ToggleFavourite(t.Id);
        SelectedCabinet?.SetFavourite(on);
        if (State.Filter == "Favourites" && !on)
        {
            // It leaves the list: the next one takes its place, or the one before when it was the last.
            Rebuild(
                Selected + 1 < Shown.Count ? Shown[Selected + 1].Id
                : Selected > 0 ? Shown[Selected - 1].Id
                : ""
            );
        }
        else if (State.IsSmart(State.Filter))
        {
            // Forgotten favourites (or a saved one) may now take this table in or leave it out.
            Rebuild(t.Id);
        }
        else
        {
            UpdateSelection();
        }
    }

    // ---- Attract sound -----------------------------------------------------------------

    /// <summary>
    /// When a cabinet has been selected for a moment, plays a few seconds of a sound file from the
    /// table's own folder (if there is one), quietly, from its backbox. Never downloads anything.
    /// </summary>
    void MaybePlayAttract()
    {
        if (Shown.Count == 0 || AttractPlaying != null)
        {
            return;
        }

        var t = Shown[Selected];
        if (!_attractFiles.TryGetValue(t.Id, out var file))
        {
            _attractFiles[t.Id] = file = LobbyArt.AttractSound(t);
        }

        if (file == null)
        {
            // A ROM table: record its own sound in the background, once; it plays when ready.
            if (_recordRoms)
            {
                _recorder.Request(t);
            }

            return;
        }
        // Read off the main thread: a recorded ROM sound is a WAV of several MB, and reading it on the main
        // thread cost the headset a frame just as the table arrived.
        if (_attractLoading == t.Id)
        {
            return;
        }

        _attractLoading = t.Id;
        var id = t.Id;
        LobbyWork
            .Run(() => LoadSound(file))
            .ContinueWith(task =>
            {
                if (!_closing)
                {
                    Callable.From(() => PlayLoadedSound(task, id, file)).CallDeferred();
                }
            });
    }

    /// <summary>Set when the lobby leaves the tree: background work finishing after that doesn't call back in.</summary>
    volatile bool _closing;

    public override void _ExitTree()
    {
        _closing = true;
        // Every way out, not just Leave: closing the desktop window (X, Alt+F4) quits without it.
        LobbyWork.StopAndWait(1500);
    }

    void PlayLoadedSound(Task<AudioStream?> task, string id, string file)
    {
        if (_attractLoading != id)
        {
            return; // moved on (or stopped) while it loaded
        }

        _attractLoading = null;
        var stream = task.IsCompletedSuccessfully ? task.Result : null;
        if (stream == null)
        {
            _attractFiles[id] = null;
            return;
        } // unreadable: don't try again
        if (_launching || AttractPlaying != null || Shown.Count == 0 || Shown[Selected].Id != id)
        {
            return;
        }

        _attract.Stream = stream;
        _attract.Position = new Vector3(
            0,
            CabinetKit.BackglassCentreY * CabinetScale,
            -(SelectedFront + CabinetLength) + CabinetKit.BoxDepth * CabinetScale
        );
        _attract.VolumeDb = -12f;
        _attract.Play();
        Trace("attract " + Path.GetFileName(file));
        AttractPlaying = file;
        _attractLeft = 10.0;
    }

    string? _attractLoading;

    static AudioStream? LoadSound(string file)
    {
        try
        {
            return Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".ogg" => AudioStreamOggVorbis.LoadFromFile(file),
                ".mp3" => new AudioStreamMP3 { Data = File.ReadAllBytes(file) },
                ".wav" => AudioStreamWav.LoadFromFile(file),
                _ => null,
            };
        }
        catch (Exception e)
        {
            GD.PushWarning($"Attract sound {file}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds the height map of every table that has a lobby video, in the background, once: building one reads the
    /// .vpx (up to a second), and doing that as you arrive at a table held its depth back. Later runs find them cached.
    /// </summary>
    void WarmPlayfieldDepth()
    {
        var jobs = Library
            .Select(t => (Video: LobbyArt.PlayfieldVideo(t), t.Source.TablePath))
            .Where(j => j.Video != null)
            .Select(j => (j.Video!.Value.Folder, j.TablePath))
            .ToList();
        if (jobs.Count == 0)
        {
            return;
        }

        LobbyWork.Run(() =>
        {
            foreach (var (folder, vpx) in jobs)
            {
                if (LobbyWork.Closing.IsCancellationRequested)
                {
                    return; // the rest are built on a later run
                }

                try
                {
                    if (!File.Exists(Path.Combine(folder, PlayfieldDepth.FileName)))
                    {
                        PlayfieldDepth.Load(folder, vpx);
                    }
                }
                catch (Exception e)
                {
                    GD.PushWarning($"Playfield depth warm-up {vpx}: {e.Message}");
                } // one table mustn't stop the rest
            }
        });
    }

    /// <summary>The selected cabinet's playfield comes alive with the table's lobby video, when it has one.</summary>
    void MaybePlayVideo()
    {
        if (
            _video.Playing != null
            || Shown.Count == 0
            || SelectedCabinet is not { Table: { } t } cab
            || _videoCheckedFor == t.Id
        )
        {
            return;
        }

        _videoCheckedFor = t.Id;
        if (LobbyArt.PlayfieldVideo(t) is var (folder, info))
        {
            _video.Play(cab, folder, info, _noDepth ? null : t.Source.TablePath);
            Trace("video start");
        }
    }

    void StopAttract()
    {
        _attractLoading = null;
        if (AttractPlaying == null)
        {
            return;
        }

        _attract.Stop();
        AttractPlaying = null;
    }

    // ---- Input / frame -------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_perfTrace)
        {
            PerfTrace(delta);
        }

        TrackMenuHold(delta);
        if (_loading != null)
        {
            if (_playing)
            {
                UpdatePlaying(delta);
            }
            else
            {
                UpdateLoading(delta);
            }
        }
        if (_returning >= 0)
        {
            WatchReturn(delta);
        }

        KeepKeyboard(delta);

        var d = (float)delta;
        if (!Mathf.IsEqualApprox(_scroll, Target))
        {
            _scroll = Mathf.Lerp(_scroll, Target, 1f - Mathf.Exp(-9f * d));
            // Ease all the way in before the last step: a cabinet travels 1.2 m per step, so snapping at 1% left
            // stopped it dead from 11 cm/s, a visible jerk in the headset. At 0.05% it's under 6 mm/s.
            if (Mathf.Abs(_scroll - Target) < SnapAt)
            {
                _scroll = Target;
                Trace("snap");
            }
            Layout();
        }
        if (Shown.Count > 0 && !_launching)
        {
            var away = Mathf.Abs(_scroll - Target);
            // The playfield video starts while the table is still coming in (decoding its first frame takes a
            // moment), so it's already playing when it arrives instead of swapping in after it stops.
            if (away < VideoFrom)
            {
                MaybePlayVideo();
                MaybeRequestWheel();
            }
            // The sound waits until it has (nearly) arrived and you've stayed on it for a moment.
            if (away < 0.02f)
            {
                _settled += delta;
                if (_settled > AttractAfter)
                {
                    MaybePlayAttract();
                }
            }
        }
        if (AttractPlaying != null)
        {
            _attractLeft -= delta;
            if (_attractLeft < 1.5)
            {
                _attract.VolumeDb = Mathf.Lerp(
                    -40f,
                    -12f,
                    Mathf.Clamp((float)_attractLeft / 1.5f, 0f, 1f)
                );
            }

            if (_attractLeft <= 0)
            {
                _attract.Stop();
                _attractLeft = double.MaxValue;
            } // played once per selection
        }
        if (_flashTime > 0)
        {
            _flashTime -= delta;
            Fade(
                _flash,
                Palette.Dmd,
                Palette.DmdPanel,
                Mathf.Clamp((float)(_flashTime / _flashFade), 0f, 0.85f)
            );
        }
        _statsCheck -= delta;
        // Not while a table starts or is played: the launch itself changes the stats (MarkPlayed, the launcher
        // counting the play), and re-showing the selection then put the ring back round the loading screen and
        // flattened the frozen playfield. ComeBack re-reads them when the room returns.
        if (_statsCheck <= 0 && !_launching)
        {
            _statsCheck = 2.0; // a file-time check every 2 s; only re-reads when the file changed
            RefreshFromStats();
        }
        if (_heightShow > 0)
        {
            _heightShow -= delta;
            Fade(
                _heightLabel,
                Colors.White,
                Colors.Black,
                Mathf.Clamp((float)_heightShow / 0.4f, 0f, 1f)
            );
        }

        // Held flippers / magna-saves act like a stick pushed all the way (so they repeat and speed up too).
        var cabX = Strongest(
            Held(CabinetAction.NextTable) - Held(CabinetAction.PrevTable),
            _padStick.X
        );
        var cabY = Strongest(
            Held(CabinetAction.PrevLetter) - Held(CabinetAction.NextLetter), // up = earlier letter
            _padStick.Y
        );
        if (_launching)
        {
            return;
        }

        if (!_vr || _left == null || _right == null || _ray == null)
        {
            Steer(cabX, cabY, delta);
            return;
        }

        var lv = _left.GetVector2("primary");
        var rv = _right.GetVector2("primary");

        // Height: while the left grip is held, the left stick's up/down raises or lowers you smoothly
        // (and does nothing else). Saved once you let go, so the file isn't rewritten every frame.
        if (_left.GetFloat("grip") > 0.6f)
        {
            var next = LobbyHeight.Step(State.HeightOffset, lv.Y, delta);
            if (next != State.HeightOffset)
            {
                SetHeight(next);
            }
            else if (_heightShow < 0.5)
            {
                ShowHeight(); // keep the readout up while gripping
            }

            lv = Vector2.Zero;
        }
        else
        {
            SaveHeight();
        }

        // Either stick, or the cabinet buttons.
        Steer(
            Strongest(Strongest(lv.X, rv.X), cabX),
            Strongest(Strongest(lv.Y, rv.Y), cabY),
            delta
        );

        // Pointing: at a cabinet for a moment selects it; at the rail highlights a group.
        int slot = -1,
            rail = -1;
        if (_ray.IsColliding() && _ray.GetCollider() is GodotObject hit)
        {
            if (hit.HasMeta("cabinet_slot"))
            {
                slot = hit.GetMeta("cabinet_slot").AsInt32();
            }
            else if (hit.HasMeta("rail_group"))
            {
                rail = hit.GetMeta("rail_group").AsInt32();
            }
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
            // Only when the hand has moved since the last selection. Otherwise a laser resting on a side
            // cabinet keeps selecting: each turn of the ring slides the next cabinet under it, and the ring
            // runs through table after table from one stick tap.
            if (
                _hoverTime > 0.25
                && (
                    _aimAtSelect == Vector3.Zero
                    || (-_ray.GlobalBasis.Z).AngleTo(_aimAtSelect) > AimMoved
                )
            )
            {
                Select(idx);
            }
        }
    }

    float Held(CabinetAction a) => _cabinetHeld.Contains(a) ? 1f : 0f;

    /// <summary>
    /// "Joystick 0: PS4 Controller (game controller)", so the log shows what the lobby can see. The GUID is SDL's,
    /// which VPX 10.8.1 names the joystick by in its mappings (SDLJoy_&lt;GUID&gt;_1).
    /// </summary>
    static void LogJoypad(int id, bool connected) =>
        GD.Print(
            connected
                ? $"Joystick {id}: {Input.GetJoyName(id)}, GUID {Input.GetJoyGuid(id)}{(Input.IsJoyKnown(id) ? " (game controller)" : " (uses the VPX joystick mapping)")}"
                : $"Joystick {id} disconnected"
        );

    /// <summary>
    /// A cabinet board's button: what VPX mapped it to on this board. Only when no connected joystick is one VPX
    /// names (older VPX, which doesn't, or a GUID Godot spells differently), any joystick's button counts.
    /// </summary>
    CabinetAction BoardAction(int device, int button)
    {
        var mapped = _cabinet.JoyDevices;
        if (mapped.Count > 0)
        {
            var guid = Input.GetJoyGuid(device).ToLowerInvariant();
            if (mapped.Contains(guid))
            {
                return _cabinet.ForJoyButton(guid, button);
            }

            if (
                Input
                    .GetConnectedJoypads()
                    .Any(id => mapped.Contains(Input.GetJoyGuid(id).ToLowerInvariant()))
            )
            {
                return CabinetAction.None; // another joystick: the cabinet's buttons are on the board VPX names
            }
        }
        return _cabinet.ForJoyButton(button);
    }

    /// <summary>A step was just taken by hand: the next one comes after the first repeat pause (see <see cref="Stick"/>).</summary>
    void PrimeRepeat(string channel)
    {
        _held[channel] = 0;
        _cool[channel] = LobbyNav.RepeatInterval(0);
    }

    public override void _Notification(int what)
    {
        // A key let go while another window had focus never arrives here: don't keep scrolling.
        if (what == NotificationApplicationFocusOut)
        {
            _cabinetHeld.Clear();
            _padStick = Vector2.Zero;
        }
    }

    /// <summary>← → turns the ring (faster while held); ↑ ↓ jumps between rail groups.</summary>
    void Steer(float x, float y, double delta)
    {
        var step = Stick("x", x, delta);
        if (step != 0)
        {
            Step(step);
            if (_held["x"] > 0.6)
            {
                ShowFlash(); // moving fast: show which letter we're passing
            }
        }
        var j = Stick("jump", y, delta);
        if (j != 0)
        {
            Select(LobbyNav.JumpWrap(Groups, Selected, -j)); // stick up = earlier letter
            ShowFlash();
        }
    }

    /// <summary>A cabinet button went down or up. Moving happens in _Process while it's held.</summary>
    void OnCabinet(CabinetAction action, bool pressed)
    {
        if (!pressed)
        {
            _cabinetHeld.Remove(action);
            return;
        }
        // While a table is played with the lobby waiting behind it, joystick boards still reach us (SDL reads
        // them in the background): the cabinet's buttons belong to the table then, Exit above all.
        if (_playing || !_cabinetHeld.Add(action))
        {
            return; // or already down
        }

        if (_launching)
        {
            // As with Menu: holding Exit while a table loads still closes the lobby.
            if (action == CabinetAction.Leave && _loading != null)
            {
                _menuHeld = 0;
                ShowFlash("HOLD TO LEAVE");
            }
            return;
        }
        switch (action)
        {
            // The first step happens on the press, so a quick tap during a slow frame (pressed and let go
            // before _Process sees it held) still moves. _Process repeats it while held, after the usual pause.
            case CabinetAction.PrevTable or CabinetAction.NextTable:
                Step(action == CabinetAction.NextTable ? 1 : -1);
                PrimeRepeat("x");
                break;
            case CabinetAction.PrevLetter or CabinetAction.NextLetter:
                Select(
                    LobbyNav.JumpWrap(Groups, Selected, action == CabinetAction.NextLetter ? 1 : -1)
                );
                ShowFlash();
                PrimeRepeat("jump");
                break;
            case CabinetAction.Play:
                _ = LaunchAsync();
                break;
            case CabinetAction.Filter:
                CycleFilter();
                break;
            case CabinetAction.Favourite:
                ToggleFavourite();
                break;
            case CabinetAction.Random:
                SelectRandom();
                break;
            // VPX's "Align VR view" (keypad 5 unless remapped), so the button that recenters a table recenters here too.
            case CabinetAction.Recenter:
                Recenter();
                break;
            // Like Menu, Exit has to be held: it's the button you just used to leave a table.
            case CabinetAction.Leave:
                _menuHeld = 0;
                ShowFlash("HOLD TO LEAVE");
                break;
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
        if (Mathf.Abs(value) < 0.6f)
        {
            return 0;
        }

        _held[channel] += delta;
        _cool[channel] -= delta;
        if (_cool[channel] > 0)
        {
            return 0;
        }

        _cool[channel] = LobbyNav.RepeatInterval(_held[channel]);
        return value > 0 ? 1 : -1;
    }

    static float Strongest(float a, float b) => Mathf.Abs(a) >= Mathf.Abs(b) ? a : b;

    void OnButton(string button, XRController3D controller)
    {
        if (_launching)
        {
            // While a table loads, holding Menu still closes the lobby (the table carries on loading).
            if (button == "menu_button" && _loading != null)
            {
                _menuHeld = 0;
                ShowFlash("HOLD TO LEAVE");
            }
            return;
        }
        switch (button)
        {
            case "trigger_click" or "ax_button":
                if (controller == _right && _hoverRail != -1)
                {
                    JumpToGroup(_hoverRail);
                    return;
                }
                if (controller == _right && _hoverSlot != -1 && _pool[_hoverSlot].Index >= 0)
                {
                    Select(_pool[_hoverSlot].Index);
                }

                _ = LaunchAsync();
                break;
            case "by_button" when controller == _left:
                SelectRandom();
                break; // Y
            case "by_button":
                CycleFilter();
                break; // B
            // The left grip is the height "shift" key, so only the right grip favourites.
            case "grip_click" when controller == _right:
                ToggleFavourite();
                break;
            case "primary_click" when controller == _left && _left!.GetFloat("grip") > 0.6f:
                ResetHeight();
                break;
            // Leaving takes a 1 s hold (see _Process): a single press, easy to make when reaching for a
            // VPX-style menu, used to close the lobby at once and looked like a crash.
            case "menu_button":
                _menuHeld = 0;
                ShowFlash("HOLD TO LEAVE");
                break;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventJoypadButton jb)
        {
            // Game controllers Godot knows (Xbox, PlayStation…) have a fixed layout; anything else (a cabinet
            // board) uses the buttons the player mapped in VPX.
            var pad = Input.IsJoyKnown(jb.Device);
            if (jb.Pressed)
            {
                GD.Print(
                    $"Joystick {jb.Device} button {(int)jb.ButtonIndex}{(pad ? " (game controller)" : "")}"
                ); // to check a board's numbering
            }

            var ja = pad
                ? CabinetButtons.ForGamepadButton((int)jb.ButtonIndex)
                : BoardAction(jb.Device, (int)jb.ButtonIndex);
            if (ja != CabinetAction.None)
            {
                OnCabinet(ja, jb.Pressed);
            }

            return;
        }

        // A game controller's left stick works like a Quest stick.
        if (@event is InputEventJoypadMotion jm && Input.IsJoyKnown(jm.Device))
        {
            if (jm.Axis == JoyAxis.LeftX)
            {
                _padStick.X = jm.AxisValue;
            }
            else if (jm.Axis == JoyAxis.LeftY)
            {
                _padStick.Y = -jm.AxisValue; // Godot's stick Y is down-positive; up = earlier letter
            }

            return;
        }

        // Keys a cabinet board sends count in VR, where no one is typing. On the desktop the keyboard keeps
        // its own keys (Ctrl+R, letters), which the cabinet defaults (Ctrl, 1, 5…) would take over.
        if (
            _vr
            && @event is InputEventKey ck
            && CabinetKeys.ToSdl(ck) is { } sdl
            && _cabinet.ForKey(sdl) is var ka and not CabinetAction.None
        )
        {
            if (!ck.Echo)
            {
                OnCabinet(ka, ck.Pressed);
            }

            return;
        }

        if (@event is not InputEventKey { Pressed: true } k)
        {
            return;
        }

        // A held key repeats: that moves along the ring and the rail and steps the height, but play, filter,
        // favourite, random and the rest happen once per press (a held F toggled the favourite 30 times a second).
        if (
            k.Echo
            && k.Keycode
                is not (
                    Key.Left
                    or Key.Right
                    or Key.Up
                    or Key.Down
                    or Key.Pageup
                    or Key.Pagedown
                    or Key.Equal
                    or Key.Minus
                    or Key.KpAdd
                    or Key.KpSubtract
                )
        )
        {
            return;
        }

        if (_launching)
        {
            if (k.Keycode == Key.Escape && _loading != null)
            {
                Leave("Escape pressed while " + _loading.Title + " was loading");
            }

            return;
        }
        switch (k.Keycode)
        {
            case Key.Right:
                Step(1);
                break;
            case Key.Left:
                Step(-1);
                break;
            case Key.Down or Key.Pagedown:
                Select(LobbyNav.JumpWrap(Groups, Selected, 1));
                ShowFlash();
                break;
            case Key.Up or Key.Pageup:
                Select(LobbyNav.JumpWrap(Groups, Selected, -1));
                ShowFlash();
                break;
            case Key.Enter or Key.KpEnter or Key.Space:
                _ = LaunchAsync();
                break;
            case Key.Escape:
                Leave("Escape pressed");
                break;
            case Key.Tab:
                CycleFilter();
                break;
            // Plain letters jump through the alphabet, so random is Ctrl+R or "?".
            case Key.R when k.CtrlPressed:
                SelectRandom();
                break;
            case Key.Slash:
                SelectRandom();
                break;
            case Key.F when !k.CtrlPressed:
                ToggleFavourite();
                break;
            case Key.Equal or Key.KpAdd:
                SetHeight(State.HeightOffset + LobbyHeight.KeyStep);
                SaveHeight();
                break;
            case Key.Minus or Key.KpSubtract:
                SetHeight(State.HeightOffset - LobbyHeight.KeyStep);
                SaveHeight();
                break;
            case Key.Home:
                ResetHeight();
                break;
            default:
                if (
                    !k.CtrlPressed
                    && k.Unicode is > 0 and < 128
                    && char.IsLetterOrDigit((char)k.Unicode)
                )
                {
                    JumpToKey((char)k.Unicode);
                }

                break;
        }
    }

    // ---- Art / launch --------------------------------------------------------------

    void PerfTrace(double delta)
    {
        _perfClock += delta;
        if (delta > 0.010)
        {
            GD.Print(
                $"SLOW {delta * 1000:0} ms at {_perfClock:0.00}s (scroll {_scroll - Target:0.000}, settled {_settled:0.00}): {string.Join(", ", _perfEvents)}"
            );
        }

        _perfEvents.Clear();
        if (_perfClock < _perfNextStep)
        {
            return;
        }

        if (_perfSteps++ == _perfStepCount)
        {
            Leave("perf trace done");
            return;
        }
        _perfNextStep = _perfClock + 3.0;
        var vp = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vp, true);
        GD.Print(
            $"STEP {_perfSteps} at {_perfClock:0.00}s: render cpu {RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp):0.0} ms, "
                + $"gpu {RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp):0.0} ms, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draws, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)} objects, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1e6:0.0} M triangles, "
                + $"{Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:0} MB video memory"
        );
        Step(1);
    }

    void OnArtLoaded(string url, Texture2D tex, Color average)
    {
        Trace("art " + Path.GetFileName(url));
        if (url == _wheelKey)
        {
            ShowWheel(tex);
        }

        foreach (var cab in _pool)
        {
            if (cab.Table == null)
            {
                continue;
            }

            if (cab.PlayfieldUrl == url)
            {
                cab.SetPlayfield(tex);
            }

            if (cab.BackglassUrl == url)
            {
                cab.SetBackglass(tex, average);
            }
        }
    }

    async Task LaunchAsync()
    {
        if (Shown.Count == 0)
        {
            return;
        }

        var t = Shown[Selected];
        if (LobbyLauncher.CheckReady(t, Settings) is { } problem)
        {
            ShowError(problem);
            return;
        }
        _launching = true;
        _keyboard.Stop(); // the table must get the keyboard: don't take it back from under it
        StopAttract();
        _video.Freeze(); // keeps the playfield's depth: dropping to flat as the table starts is a jarring change
        _recorder.StopAll();
        var wait = Settings.LobbyWaitsForTable;
        if (wait)
        {
            _statusFile = PlayStatus.PathBeside(_settingsPath);
            StartLoading(t);
        }
        else
        {
            _header.Text = "Starting " + t.Title;
            PlaceWheel(); // a longer title may wrap to another line
            _status.Text = "";
            await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);
        }
        if (LobbyLauncher.Launch(t, Settings, _settingsPath, _statePath, _statusFile) is { } error)
        {
            StopLoading();
            _launching = false;
            // Without a loading screen StopLoading does nothing: show the table again here ("Starting …" off the
            // title, the frozen video started again), then the error under it.
            UpdateSelection();
            ShowError(error);
            return;
        }
        State.MarkPlayed(t.Id);
        if (!wait)
        {
            Leave("starting " + t.Title); // ends our OpenXR session so the table gets the headset
        }
    }

    // ---- Loading screen --------------------------------------------------------------

    /// <summary>The loading screen's lamps: a row of bulbs under the status text, lit one after another like an attract-mode chase.</summary>
    void BuildChase()
    {
        const int count = 15;
        const float gap = 0.075f;
        var mesh = new SphereMesh
        {
            Radius = 0.016f,
            Height = 0.032f,
            RadialSegments = 12,
            Rings = 6,
        };
        for (var i = 0; i < count; i++)
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = Palette.DmdPanel,
            };
            _chaseLamps.Add(mat);
            _chase.AddChild(
                new MeshInstance3D
                {
                    Mesh = mesh,
                    MaterialOverride = mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = new Vector3((i - (count - 1) / 2f) * gap, 0, 0),
                }
            );
        }
        _chase.Position = new Vector3(0, 2.1f, -1.8f) + AboveCabinet;
        _chase.Visible = false;
        AddChild(_chase);
    }

    /// <summary>
    /// Shows the table you picked, alone in the room, while it loads: the ring, rail and help go dark (which
    /// also leaves the GPU to the table). The lobby keeps its OpenXR session until the table opens its own and
    /// the runtime moves focus to it, so the headset never drops to an empty view in between.
    /// </summary>
    void StartLoading(LobbyTable t)
    {
        _loading = t;
        _error = null; // an old failure of this table mustn't come back after it plays fine
        _loadingTime = _unfocused = 0;
        _loggedXrState = null;
        _loadingPoll = 0.25;
        foreach (var cab in _pool)
        {
            cab.Visible = cab.Index == Selected && cab.Table == t;
        }

        _rail.Visible = _hint.Visible = _scores.Visible = _scoresPanel.Visible = false;
        _chase.Visible = true;
        _header.Text = t.Title;
        PlaceWheel();
        _status.Modulate = Palette.Text;
        ShowLoadingText();
        StartLoadingVideo(t);
        GD.Print($"Lobby waiting for {t.Title} to take over the headset");
    }

    /// <summary>
    /// The table's loading video (or the one for every table), on a big screen where the cabinet stood: the cabinet
    /// steps aside for it, and its title, lamps and logo stay above the screen.
    /// </summary>
    void StartLoadingVideo(LobbyTable t)
    {
        LoadingVideo.Video? video;
        try
        {
            video = LobbyArt.LoadingVideoFor(t, Settings);
        }
        catch (Exception e)
        {
            GD.PushWarning($"Loading video for {t.Title}: {e.Message}");
            return;
        }
        if (video == null)
        {
            return;
        }

        foreach (var cab in _pool)
        {
            cab.Visible = false;
        }

        _loadingVideo.Play(video);
        // Its top just under the lamps (which rise with bigger cabinets), so the screen sits a little below
        // eye level, the comfortable way to watch, and nothing floats over the picture.
        var top = Math.Min(LoadingScreenTop, _chase.Position.Y - 0.12f);
        _loadingVideo.Position = new Vector3(
            0,
            top - _loadingVideo.Size.Y / 2,
            LoadingScreenZ + AboveCabinet.Z
        );
        GD.Print(
            $"Loading video: {video.Folder} ({video.Info.Frames} frames{(video.Audio != null ? ", with sound" : "")})"
        );
    }

    const float LoadingScreenTop = 2.0f,
        LoadingScreenZ = -2.7f;

    public string? LoadingVideoPlaying => _loadingVideo.Playing;
    public int LoadingVideoFrames => _loadingVideo.FramesShown;
    public bool LoadingVideoSound => _loadingVideo.SoundPlaying;

    /// <summary>Self-test: the loading screen for the selected table, reading <paramref name="statusFile"/>, without launching anything.</summary>
    /// <summary>What a launch does to the playfield video (LaunchAsync), without launching anything.</summary>
    public void FreezeVideoForTest() => _video.Freeze();

    public void StartLoadingForTest(string statusFile)
    {
        _launching = true;
        _statusFile = statusFile;
        StartLoading(Shown[Selected]);
    }

    public bool RailVisible => _rail.Visible;
    public int VisibleCabinets => _pool.Count(c => c.Visible);

    /// <summary>Back to the ring: the table failed or closed before it reached the headset.</summary>
    void StopLoading()
    {
        if (_loading == null)
        {
            return;
        }

        _loading = null;
        _statusFile = null;
        _launching = false;
        _loadingVideo.Stop();
        foreach (var cab in _pool)
        {
            cab.Visible = cab.Index >= 0;
        }

        _rail.Visible = _hint.Visible = _scores.Visible = true;
        _chase.Visible = false;
        UpdateSelection();
    }

    OpenXRInterface.SessionState? _loggedXrState;

    void ShowLoadingText() =>
        _status.Text =
            $"Loading{new string('.', 1 + (int)(_loadingTime * 2) % 3), -3}  {(int)_loadingTime} s";

    void UpdateLoading(double delta)
    {
        _loadingTime += delta;
        var n = _chaseLamps.Count;
        var head = (int)(_loadingTime * 12) % n;
        for (var i = 0; i < n; i++)
        {
            var glow = ((head - i + n) % n) switch
            {
                0 => 1f,
                1 => 0.5f,
                2 => 0.22f,
                _ => 0.06f,
            };
            _chaseLamps[i].AlbedoColor = Palette.DmdPanel.Lerp(Palette.Lamp, glow);
        }

        _loadingPoll -= delta;
        if (_loadingPoll > 0)
        {
            return;
        }

        _loadingPoll = 0.25;
        var t = _loading!;
        var st = _statusFile != null ? PlayStatus.Read(_statusFile) : null;
        if (st?.State == PlayStatus.Failed)
        {
            StopLoading();
            ShowError($"{t.Title} didn't start: {st.Message}");
            return;
        }
        if (st?.State == PlayStatus.Exited)
        {
            StopLoading();
            ShowError(
                $"{t.Title} closed while it was loading. Try playing it from VR Table Manager to see what went wrong."
            );
            return;
        }
        ShowLoadingText();
        if (_loadingVideo.Failed)
        {
            // Not one of its frames could be read: the plain loading screen, with the table back in place.
            _loadingVideo.Stop();
            foreach (var cab in _pool)
            {
                cab.Visible = cab.Index == Selected && cab.Table == t;
            }
        }

        // The engine is running (or the manager can't write its status, and it's had time to start).
        var running = st?.State == PlayStatus.Running || (st == null && _loadingTime > 5);
        if (!_vr)
        {
            if (running)
            {
                Leave("started " + t.Title);
            }

            return;
        }
        // The runtime gives the headset's focus to the newest app, so once the table's own session starts ours
        // drops out of focus. VPX starts its session before it has loaded the table, so while the runtime still
        // draws the lobby the loading screen stays up (LobbyHandover). (STOPPING is handled in SetupView.)
        var state = _xr?.GetSessionState();
        if (state != _loggedXrState)
        {
            // What the runtime does around the hand-over, for when it doesn't look right in the headset.
            GD.Print(
                $"OpenXR session {state} at {_loadingTime:0.0} s (table {st?.State ?? "no status"})"
            );
            _loggedXrState = state;
        }
        var shown = state switch
        {
            OpenXRInterface.SessionState.Focused => LobbyShown.Focused,
            OpenXRInterface.SessionState.Visible => LobbyShown.Visible,
            _ => LobbyShown.Hidden,
        };
        if (running && _xr != null && shown != LobbyShown.Focused)
        {
            // The table has the focus: the picture can stay up while it loads, but not the sound, which could
            // otherwise play on under the table if the runtime keeps showing the lobby.
            if (_loadingVideo.SoundPlaying)
            {
                _loadingVideo.Mute();
            }
            _unfocused += 0.25;
            if (LobbyHandover.StepAside(running, shown, _unfocused))
            {
                GD.Print($"Lobby stepping aside: {shown} for {_unfocused:0.0} s");
                if (Settings.LobbyStaysOpen && _statusFile != null)
                {
                    StartPlaying(t);
                }
                else
                {
                    Leave(t.Title + " took over the headset");
                }

                return;
            }
        }
        else
        {
            _unfocused = 0;
        }

        if (_loadingTime > LoadingGiveUp)
        {
            Leave($"still waiting for {t.Title} after {LoadingGiveUp:0} s");
        }
    }

    /// <summary>
    /// The table has the headset. The lobby keeps running behind it with every part of the room hidden, which is
    /// next to nothing to draw (and the runtime doesn't show it anyway). When the table closes, the runtime gives
    /// focus back to the lobby, which is still open on the same table.
    /// </summary>
    void StartPlaying(LobbyTable t)
    {
        _playing = true;
        _keyboard.Stop(); // the table has the keyboard now
        // A held Exit (or flipper) stops counting: those buttons are the table's now.
        _cabinetHeld.Clear();
        _padStick = Vector2.Zero;
        _loadingPoll = 0.5;
        _loadingVideo.Stop(); // its sound would play on under the table: hiding doesn't stop sound
        _hidden.Clear();
        foreach (var child in GetChildren())
        {
            if (child is Node3D n && n != _rig)
            {
                _hidden.Add((n, n.Visible));
                n.Visible = false;
            }
        }

        _scoreLines.Remove(t.Id); // its high scores can change while it's played
        GD.Print($"Lobby waiting behind {t.Title}");
    }

    void UpdatePlaying(double delta)
    {
        _loadingPoll -= delta;
        if (_loadingPoll > 0)
        {
            return;
        }

        _loadingPoll = 0.5;
        var st = _statusFile != null ? PlayStatus.Read(_statusFile) : null;
        if (st?.State is PlayStatus.Exited or PlayStatus.Failed)
        {
            ComeBack(_loading!.Title + " closed");
        }
        // No status to go by: the headset's focus coming back means the table is gone.
        else if (st == null && _xr?.GetSessionState() == OpenXRInterface.SessionState.Focused)
        {
            ComeBack("focus came back");
        }
    }

    /// <summary>The table closed: the room comes back on the table you played, and we wait for the headset's focus.</summary>
    void ComeBack(string why)
    {
        foreach (var (n, was) in _hidden)
        {
            n.Visible = was;
        }

        _hidden.Clear();
        _playing = false;
        _cabinetHeld.Clear();
        _padStick = Vector2.Zero;
        // VPX's window closing hands focus to whatever is next; keyboard-type cabinet boards need it here.
        TakeKeyboard();
        _wheelKeys.Clear(); // wheel art may have been added in the manager while the table played
        StopLoading(); // the ring, rail and the table's stats again
        RefreshFromStats();
        _returning = 0;
        GD.Print("Lobby back: " + why);
    }

    /// <summary>Starts taking the desktop window's keyboard back; KeepKeyboard does the trying, every frame.</summary>
    void TakeKeyboard() => _keyboard.Start();

    void KeepKeyboard(double delta)
    {
        if (!_keyboard.Active)
        {
            return;
        }

        var inFront = WindowFocus.IsInFront();
        if (_keyboard.Tick(delta, inFront) && WindowFocus.BringToFront())
        {
            GD.Print($"Lobby has the keyboard (try {_keyboard.Attempts})");
        }
        else if (!_keyboard.Active && _keyboard.GaveUp)
        {
            GD.PushWarning(
                $"Lobby couldn't get the keyboard back after {_keyboard.Attempts} tries; click its window on the desktop"
            );
        }
    }

    /// <summary>
    /// The runtime should hand the headset back once the table's session ends. If it hasn't after a few seconds
    /// (some runtimes stop a session that lost focus for good), a fresh lobby is started on the same table.
    /// </summary>
    void WatchReturn(double delta)
    {
        _returning += delta;
        if (!_vr || _xr?.GetSessionState() == OpenXRInterface.SessionState.Focused)
        {
            _returning = -1;
            return;
        }
        if (_returning < ReturnGiveUp)
        {
            return;
        }

        _returning = -1;
        var id = Shown.Count > 0 ? Shown[Selected].Id : "";
        if (LobbyLauncher.Restart(id, _settingsPath, _statePath) is { } error)
        {
            GD.PushWarning("Couldn't restart the lobby: " + error);
        }
        else
        {
            Leave($"the headset didn't come back after {ReturnGiveUp:0} s; started a fresh lobby");
        }
    }

    /// <summary>Self-test: what happens when the table takes over the headset, without a headset.</summary>
    public void StartPlayingForTest() => StartPlaying(_loading!);

    public bool RoomVisible => _ring.Visible;

    (string TableId, string Message)? _error;

    void ShowError(string message)
    {
        _error = Shown.Count > 0 ? (Shown[Selected].Id, message) : null;
        _status.Text = message;
        _status.Modulate = Palette.Red.Lightened(0.25f);
    }

    static Dictionary<string, string> UserArgs()
    {
        var d = new Dictionary<string, string>();
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (!a.StartsWith("--"))
            {
                continue;
            }

            var eq = a.IndexOf('=');
            if (eq > 2)
            {
                d[a[2..eq]] = a[(eq + 1)..];
            }
            else
            {
                d[a[2..]] = "";
            }
        }
        return d;
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>
/// The lobby's background work that uses Godot's Image (art, tables' cabinets, height maps, video frames, sounds).
/// When the lobby closes (Main.Leave) it's told to stop through <see cref="Closing"/> and given a moment to finish,
/// so the engine never shuts down under a worker still inside an Image call.
/// </summary>
public static class LobbyWork
{
    static readonly CancellationTokenSource Stop = new();
    static readonly List<Task> Running = [];

    /// <summary>Set once the lobby is closing: work not started yet doesn't start, work under way stops when it can.</summary>
    public static CancellationToken Closing => Stop.Token;

    public static Task Run(Action work) => Start<Task>(() => Task.Run(work), Task.FromCanceled);

    public static Task Run(Func<Task> work) => Start<Task>(() => Task.Run(work), Task.FromCanceled);

    public static Task<T> Run<T>(Func<T> work) =>
        Start<Task<T>>(() => Task.Run(work), Task.FromCanceled<T>);

    /// <summary>
    /// Starts the work and keeps it to wait for; once the lobby is closing, it doesn't start at all (a cancelled task).
    /// Under the lock, so nothing can start between StopAndWait cancelling and it looking at what's running.
    /// </summary>
    static TTask Start<TTask>(Func<TTask> start, Func<CancellationToken, TTask> cancelled)
        where TTask : Task
    {
        lock (Running)
        {
            if (Closing.IsCancellationRequested)
            {
                return cancelled(Closing);
            }

            Running.RemoveAll(t => t.IsCompleted);
            var task = start();
            Running.Add(task);
            return task;
        }
    }

    /// <summary>Tells the work to stop and waits for it, at most <paramref name="milliseconds"/>.</summary>
    public static void StopAndWait(int milliseconds)
    {
        Stop.Cancel();
        Task[] left;
        lock (Running)
        {
            left = [.. Running.Where(t => !t.IsCompleted)];
        }
        try
        {
            Task.WaitAll(left, milliseconds);
        }
        catch (AggregateException) { } // work that failed or was cancelled has stopped all the same
    }
}
