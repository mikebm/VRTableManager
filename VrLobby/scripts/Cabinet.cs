using Godot;
using VrTableManager.Lobby;

namespace VrLobby;

/// <summary>
/// One pinball cabinet in the ring. The ring keeps a small pool of these and re-assigns them to
/// tables as you scroll, so only ~11 exist however big the library is.
/// </summary>
public partial class Cabinet : Node3D
{
    public int Slot { get; private set; }
    /// <summary>Position of the table in the shown list, or -1 when this cabinet is parked.</summary>
    public int Index { get; private set; } = -1;
    public LobbyTable? Table { get; private set; }
    public string? PlayfieldUrl { get; private set; }
    public string? BackglassUrl { get; private set; }
    public bool HasPlayfieldArt => _playfieldMat.AlbedoTexture != null;
    public bool HasBackglassArt => _backglassMat.AlbedoTexture != null;
    public bool ShowsTitlePlate => _plate.Visible;
    public bool ShowsFavourite => _star.Visible;
    public Color BodyColour => _bodyMat.AlbedoColor;
    /// <summary>Colour of the backbox strip, coin lights and flipper buttons.</summary>
    public Color LampColour => _glowMat.AlbedoColor;

    readonly StandardMaterial3D _bodyMat = new() { Roughness = 0.45f, Metallic = 0.15f };
    readonly StandardMaterial3D _glowMat = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, EmissionEnabled = true };
    // Playfield and backglass are unshaded: the art carries its own lighting (and a backglass is backlit),
    // so dimming is done by tinting them rather than by the room lights.
    // Anisotropic filtering keeps a playfield sharp when you look along it at a low angle.
    readonly StandardMaterial3D _playfieldMat = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic };
    readonly StandardMaterial3D _backglassMat = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic };
    readonly Label3D _plate = new();
    readonly Label3D _star = new();
    readonly Label3D _dmd = new();

    readonly List<Node3D> _generic = [];          // the generic cabinet's parts (hidden while a real cabinet shows)
    readonly QuadMesh _playfieldQuad = new();
    readonly QuadMesh _backglassQuad = new();
    MeshInstance3D _backglassPanel = null!;
    MeshInstance3D _playfield = null!;
    MeshInstance3D _real = null!;

    /// <summary>True while this slot shows the table's own cabinet from its .vpx.</summary>
    public bool ShowsRealCabinet => _real.Visible && _real.Mesh != null;
    public RealCabinet? Real { get; private set; }
    /// <summary>True when this table has no VR cabinet of its own and shows the template cabinet with its own art.</summary>
    public bool Borrowed { get; private set; }
    public Vector3 PlayfieldPosition => _playfield.Position;

    Color _baseBody = Palette.SurfaceHi;
    bool _selected;   // lights the backbox strip, coin lights and flipper buttons in lamp amber
    float _bright = 1f;

    static readonly Color DarkPlayfield = Palette.Surface;
    static readonly Color DarkGlass = Palette.DmdPanel;

    public void Build(int slot, Font? displayFont, Font? dmdFont)
    {
        Slot = slot;
        AddChild(new MeshInstance3D { Mesh = CabinetKit.Shadow, MaterialOverride = CabinetKit.ShadowMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        Generic(new MeshInstance3D { Mesh = CabinetKit.Body, MaterialOverride = _bodyMat });
        Generic(new MeshInstance3D { Mesh = CabinetKit.Metal, MaterialOverride = CabinetKit.MetalMaterial });
        Generic(new MeshInstance3D { Mesh = CabinetKit.Chrome, MaterialOverride = CabinetKit.ChromeMaterial });
        Generic(new MeshInstance3D { Mesh = CabinetKit.Grille, MaterialOverride = CabinetKit.GrilleMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        Generic(new MeshInstance3D { Mesh = CabinetKit.Glow, MaterialOverride = _glowMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        // Playfield: a portrait quad, image top towards the back. On the generic cabinet it lies on the
        // sloped floor of the well; on a table's own cabinet it moves to that cabinet's playfield.
        var wellZ = CabinetKit.WellCentreZ;
        var floorY = CabinetKit.TopAt(wellZ);
        var tilt = CabinetKit.Slope - Mathf.Pi / 2;
        _playfield = new MeshInstance3D
        {
            Mesh = _playfieldQuad,
            MaterialOverride = _playfieldMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_playfield);
        PlaceGenericPlayfield();
        // The table's own cabinet, when it has one (see ShowReal).
        _real = new MeshInstance3D { Visible = false };
        AddChild(_real);
        // Glass lid, just under the rail tops, spanning the well.
        Generic(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(CabinetKit.Width - 2 * CabinetKit.WallThick, CabinetKit.WellLength) },
            MaterialOverride = CabinetKit.GlassMaterial,
            Position = new Vector3(0, floorY + CabinetKit.GlassLift, wellZ),
            Rotation = new Vector3(tilt, 0, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        // Backglass panel in the backbox (also used on a table's own backbox when its cabinet has no backglass).
        _backglassPanel = new MeshInstance3D
        {
            Mesh = _backglassQuad,
            MaterialOverride = _backglassMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        Generic(_backglassPanel);
        PlaceGenericBackglass();

        // Lit title plate, used when there's no backglass picture.
        _plate.Font = displayFont;
        _plate.FontSize = 90;
        _plate.PixelSize = 0.0011f;
        _plate.Width = CabinetKit.BackglassSize.X / 0.0011f * 0.9f;
        _plate.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _plate.HorizontalAlignment = HorizontalAlignment.Center;
        _plate.OutlineSize = 0;
        _plate.Modulate = Palette.Lamp;

        AddChild(_plate);
        _generic.Add(_plate);

        // Dot-matrix display between the speakers: the table's name in DMD orange, in the Doto dot-matrix font.
        _dmd.Font = dmdFont;
        _dmd.FontSize = 40;
        _dmd.PixelSize = 0.00065f;
        _dmd.Width = CabinetKit.DmdSize.X / 0.00065f * 0.92f;
        _dmd.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _dmd.HorizontalAlignment = HorizontalAlignment.Center;
        _dmd.OutlineSize = 0;
        _dmd.Position = new Vector3(0, CabinetKit.PanelCentreY, CabinetKit.BoxFront + 0.0065f);
        AddChild(_dmd);
        _generic.Add(_dmd);

        _star.Text = "★";
        _star.FontSize = 120;
        _star.PixelSize = 0.0012f;
        _star.OutlineSize = 14;
        _star.Modulate = Palette.Lamp;
        _star.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        _star.Position = new Vector3(0, CabinetKit.Height + 0.12f, CabinetKit.Back + 0.13f);
        AddChild(_star);

        // One collider for the whole cabinet, so pointing anywhere at it selects it.
        var area = new Area3D();
        area.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(CabinetKit.Width + 0.06f, CabinetKit.Height, CabinetKit.Length) },
            Position = new Vector3(0, CabinetKit.Height / 2, 0),
        });
        area.SetMeta("cabinet_slot", slot);
        AddChild(area);

        Park();
    }

    void Generic(Node3D part)
    {
        AddChild(part);
        _generic.Add(part);
    }

    void PlaceGenericPlayfield()
    {
        var wellZ = CabinetKit.WellCentreZ;
        _playfieldQuad.Size = CabinetKit.PlayfieldSize;
        _playfield.Position = new Vector3(0, CabinetKit.TopAt(wellZ) + 0.004f, wellZ);
        _playfield.Rotation = new Vector3(CabinetKit.Slope - Mathf.Pi / 2, 0, 0);
        _star.Position = new Vector3(0, CabinetKit.Height + 0.12f, CabinetKit.Back + 0.13f);
    }

    /// <summary>
    /// Shows the table's own VR cabinet in place of the generic one (or goes back to generic with null).
    /// The playfield picture moves onto that cabinet's playfield, which VR cabinets keep level.
    /// </summary>
    public void ShowReal(RealCabinet? real, bool borrowed = false)
    {
        Real = real;
        Borrowed = real != null && borrowed;
        if (real == null)
        {
            _real.Visible = false;
            _real.Mesh = null;
            foreach (var g in _generic) g.Visible = true;
            _plate.Visible = BackglassUrl == null && !HasBackglassArt;
            PlaceGenericPlayfield();
            PlaceGenericBackglass();
            return;
        }
        _real.Mesh = real.Mesh;
        for (var i = 0; i < real.Mesh.GetSurfaceCount(); i++) _real.SetSurfaceOverrideMaterial(i, null); // none left from another table
        _real.Visible = true;
        foreach (var g in _generic) g.Visible = false;
        _playfieldQuad.Size = real.PlayfieldSize;
        _playfield.Position = real.PlayfieldCentre + new Vector3(0, 0.003f, 0);
        _playfield.Rotation = new Vector3(-Mathf.Pi / 2, 0, 0);
        _star.Position = new Vector3(0, real.Bounds.End.Y + 0.12f, real.Bounds.Position.Z + 0.13f);

        // Backglass: the cabinet's own picture when it has one. Some VR tables set it from their script
        // (so the file has none) or draw it another way: then the game's VPS backglass fills in, on the
        // cabinet's backglass part, or as a panel on the front of its backbox.
        if (Borrowed) DressBorrowed(real);
        if (real.BackglassSurface >= 0 && !Borrowed && !real.BackglassTextured)
            _real.SetSurfaceOverrideMaterial(real.BackglassSurface, _backglassMat);
        // A borrowed cabinet may print its own backglass on the backbox itself, so this table's backglass
        // always goes on a panel on the backbox front.
        else if ((Borrowed || real.BackglassSurface < 0) && real.Backbox is { } box)
        {
            var h = Mathf.Min(box.Size.Y * 0.62f, 0.6f);
            _backglassQuad.Size = new Vector2(Mathf.Min(h * 4 / 3, box.Size.X * 0.92f), h);
            var y = box.End.Y - 0.04f - h / 2;
            _backglassPanel.Position = new Vector3(box.GetCenter().X, y, box.End.Z + 0.004f);
            _backglassPanel.Visible = true;
            _plate.Position = _backglassPanel.Position + new Vector3(0, 0, 0.002f);
            _plate.Visible = BackglassUrl == null && !HasBackglassArt;
        }
    }

    static StandardMaterial3D? _dmdPanelMat;
    static StandardMaterial3D DmdPanelMaterial => _dmdPanelMat ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Palette.DmdPanel,
    };

    /// <summary>
    /// The template cabinet (e.g. Medieval Madness) dressed for this table: its printed art becomes this
    /// table's side colour (from its backglass), its speaker grilles plain grille, its display a dark panel.
    /// Metal, chrome, legs and the coin door stay as they are.
    /// </summary>
    void DressBorrowed(RealCabinet real)
    {
        for (var i = 0; i < real.Kinds.Count; i++)
        {
            Material? m = real.Kinds[i] switch
            {
                SurfaceKind.Art => _bodyMat,
                SurfaceKind.Grille => CabinetKit.GrilleMaterial,
                SurfaceKind.Dmd => DmdPanelMaterial,
                SurfaceKind.Backglass => DmdPanelMaterial,   // the panel below goes in front (see ShowReal)
                _ => null,
            };
            _real.SetSurfaceOverrideMaterial(i, m);
        }
    }

    void PlaceGenericBackglass()
    {
        _backglassQuad.Size = CabinetKit.BackglassSize;
        _backglassPanel.Position = new Vector3(0, CabinetKit.BackglassCentreY, CabinetKit.BoxFront - 0.002f);
        _plate.Position = new Vector3(0, CabinetKit.BackglassCentreY, CabinetKit.BoxFront + 0.002f);
    }

    /// <summary>Shows a table on this cabinet. Art arrives later through SetPlayfield / SetBackglass.</summary>
    public void Assign(int index, LobbyTable table, string? playfieldUrl, string? backglassUrl, bool favourite)
    {
        Visible = true;
        var same = Table == table && Index == index;
        Index = index;
        Table = table;
        _star.Visible = favourite;
        if (same && playfieldUrl == PlayfieldUrl && backglassUrl == BackglassUrl) return;

        PlayfieldUrl = playfieldUrl;
        BackglassUrl = backglassUrl;
        _playfieldMat.AlbedoTexture = null;
        _backglassMat.AlbedoTexture = null;
        _plate.Text = table.Title;
        _dmd.Text = table.Title.ToUpperInvariant();
        ShowReal(null); // a different table: generic until its own cabinet is ready
        _plate.Visible = backglassUrl == null;
        _baseBody = EngineColour(table.Badge);
        ApplyColours();
    }

    public void Park()
    {
        Visible = false;
        Index = -1;
        Table = null;
        PlayfieldUrl = BackglassUrl = null;
    }

    public void SetPlayfield(Texture2D tex)
    {
        _playfieldMat.AlbedoTexture = tex;
        ApplyColours();
    }

    /// <summary>The backglass also gives the cabinet its side colour (a darkened average of the art).</summary>
    public void SetBackglass(Texture2D tex, Color average)
    {
        _backglassMat.AlbedoTexture = tex;
        _plate.Visible = false;
        _baseBody = SideColour(average);
        ApplyColours();
    }

    public void SetFavourite(bool on) => _star.Visible = on;

    /// <summary>1 for the selected cabinet, fading to ~0.35 for the ones around the ring.</summary>
    public void SetFocus(float focus, bool selected)
    {
        _bright = Mathf.Lerp(0.35f, 1f, Mathf.Clamp(focus, 0f, 1f));
        _selected = selected;
        ApplyColours();
    }

    void ApplyColours()
    {
        var b = _bright;
        _bodyMat.AlbedoColor = _baseBody * new Color(b, b, b);
        _playfieldMat.AlbedoColor = (_playfieldMat.AlbedoTexture == null ? DarkPlayfield : Colors.White) * new Color(b, b, b);
        _backglassMat.AlbedoColor = (_backglassMat.AlbedoTexture == null ? DarkGlass : Colors.White) * new Color(b, b, b);
        // The selected cabinet lights up in lamp amber; the others glow in their table type's insert colour.
        var lamp = Table == null ? Palette.Blue : _selected ? Palette.Lamp : Palette.Insert(Table.Badge);
        _glowMat.AlbedoColor = lamp;
        _glowMat.Emission = lamp;
        _glowMat.EmissionEnergyMultiplier = 0.3f + 0.9f * (b - 0.35f) / 0.65f;
        _plate.Modulate = new Color(Palette.Lamp, b);
        _dmd.Modulate = new Color(Palette.Dmd * new Color(b, b, b), 1f);
    }

    static Color SideColour(Color average)
    {
        // Keep the hue, but dark and a little saturated, like printed side art in a dim room.
        average.ToHsv(out var h, out var s, out var v);
        return Color.FromHsv(h, Mathf.Clamp(s * 1.1f, 0.3f, 0.85f), Mathf.Clamp(v * 0.6f, 0.16f, 0.42f));
    }

    /// <summary>Until the backglass arrives: charcoal with a hint of the table type's insert colour.</summary>
    static Color EngineColour(string badge) => Palette.SurfaceHi.Lerp(Palette.Insert(badge), 0.18f);
}

/// <summary>
/// The "DMD & lamps" identity shared with VR Table Manager (see its App.axaml): neutral black and
/// charcoal, DMD plasma orange for display text, incandescent lamp amber for anything lit or
/// selected, playfield-insert colours for table types, and chrome for trim.
/// </summary>
public static class Palette
{
    public static readonly Color Bg = new("#0A0A0B");
    public static readonly Color Surface = new("#141416");
    public static readonly Color SurfaceHi = new("#1E1E21");
    public static readonly Color Line = new("#2A2A2F");
    public static readonly Color LineHi = new("#3D3D44");
    public static readonly Color Text = new("#EEEEF1");
    public static readonly Color Muted = new("#9B9BA4");
    public static readonly Color Dmd = new("#FF7A1F");
    public static readonly Color DmdPanel = new("#120803");
    public static readonly Color Lamp = new("#FFB23E");
    public static readonly Color Blue = new("#3F95E8");
    public static readonly Color Red = new("#E5483B");
    public static readonly Color Green = new("#63C46E");

    /// <summary>Insert colour for a table type: blue VPX, red Future Pinball, green Pinball FX / M.</summary>
    public static Color Insert(string badge) => badge switch
    {
        "FP" => Red,
        "FX" or "M" => Green,
        _ => Blue,
    };
}
