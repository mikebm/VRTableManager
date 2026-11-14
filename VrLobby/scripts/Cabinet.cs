using Godot;
using VrTableManager.Lobby;
using VrTableManager.Services;

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
    public bool HasPlayfieldArt => _playfieldArt != null;

    /// <summary>The still picture; a playing lobby video (<see cref="SetPlayfieldVideo"/>) shows over it.</summary>
    Texture2D? _playfieldArt;
    public bool ShowsPlayfieldVideo =>
        _playfieldMat.AlbedoTexture != null && _playfieldMat.AlbedoTexture != _playfieldArt;

    /// <summary>Colour at the centre of what the playfield shows (tests).</summary>
    public Color? PlayfieldTextureColour() =>
        _playfieldMat.AlbedoTexture?.GetImage() is { } img
            ? img.GetPixel(img.GetWidth() / 2, img.GetHeight() / 2)
            : null;

    public bool HasBackglassArt => _backglassMat.AlbedoTexture != null;
    public bool ShowsTitlePlate => _plate.Visible;
    public bool ShowsFavourite => _star.Visible;
    public Color BodyColour => _bodyMat.AlbedoColor;

    /// <summary>Colour of the backbox strip, coin lights and flipper buttons.</summary>
    public Color LampColour => _glowMat.AlbedoColor;

    // Both sides, like every VR cabinet material (RealCabinetLoader.MaterialFor): it also paints a borrowed cabinet's
    // art surfaces (DressBorrowed), and those include single-sided panels seen from behind. Drawn one-sided, the back
    // blade behind the playfield vanished and the floor showed through (Terminator 3 in the Medieval Madness cabinet).
    readonly StandardMaterial3D _bodyMat = new()
    {
        Roughness = 0.45f,
        Metallic = 0.15f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    readonly StandardMaterial3D _glowMat = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        EmissionEnabled = true,
    };

    // Playfield and backglass are unshaded: the art carries its own lighting (and a backglass is backlit),
    // so dimming is done by tinting them rather than by the room lights.
    // Anisotropic filtering keeps a playfield sharp when you look along it at a low angle.
    readonly StandardMaterial3D _playfieldMat = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
    };

    // Both sides: it also goes on a VR cabinet's own backglass part, which may face either way (Harry Potter's does).
    readonly StandardMaterial3D _backglassMat = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    readonly Label3D _plate = new();
    readonly Label3D _star = new();

    readonly Node3D _body = new(); // everything drawn, hidden while the table's own cabinet loads
    readonly List<Node3D> _generic = []; // the generic cabinet's parts (hidden while a real cabinet shows)

    // The generic backbox (CabinetKit.BuildBackbox), on the drawn cabinet and on a borrowed one in place of its own
    // backbox: a plain box whose front is the backglass picture. _boxBounds is where it stands (the backbox it
    // replaces), null while a table's own cabinet shows; BackboxBounds is the box as shaped to the picture.
    readonly Node3D _box = new();
    readonly MeshInstance3D _boxBody = new();
    Aabb? _boxBounds;

    /// <summary>True while this cabinet wears the generic backbox (the drawn cabinet, or a borrowed one).</summary>
    public bool ShowsGenericBackbox => _boxBounds != null && _box.Visible;

    /// <summary>The generic backbox as shaped to the current picture (tests).</summary>
    public Aabb? BackboxBounds { get; private set; }

    /// <summary>The backglass picture's panel: centre and size (tests).</summary>
    public (Vector3 Centre, Vector2 Size) BackglassRect =>
        (_backglassPanel.Position, _backglassQuad.Size);

    /// <summary>Share of the backglass picture's height cut off at the bottom as speaker grille (tests).</summary>
    public float BackglassCrop => 1f - _backglassMat.Uv1Scale.Y;

    /// <summary>Speaker-grille share of this backglass picture (a .directb2s's GrillHeight; see ArtLoader.GrilleShare).</summary>
    float _grilleShare;
    readonly QuadMesh _playfieldQuad = new();
    readonly QuadMesh _backglassQuad = new();
    MeshInstance3D _backglassPanel = null!;
    MeshInstance3D _playfield = null!;

    /// <summary>Set while the playfield shows depth (<see cref="SetPlayfieldDepth"/>); draws in place of _playfieldMat.</summary>
    ShaderMaterial? _depthMat;

    /// <summary>True while the playfield is drawn with its height map (tests).</summary>
    public bool ShowsPlayfieldDepth => _depthMat != null;
    MeshInstance3D _real = null!;
    MeshInstance3D _realBox = null!;

    /// <summary>How tall the default cabinet's backbox is drawn, as a share of its own (1, or less for a wide whole-front picture).</summary>
    public float BackboxHeightShare => _realBox.Visible ? _realBox.Scale.Y : 1f;

    /// <summary>True while the slot shows nothing: its table has a VR cabinet that's still being read.</summary>
    public bool Loading => !_body.Visible;

    /// <summary>Shows nothing until the table's own cabinet is ready, rather than a stand-in that then swaps.</summary>
    public void ShowLoading()
    {
        Real = null;
        Borrowed = false;
        _body.Visible = false;
    }

    /// <summary>True while this slot shows the table's own cabinet from its .vpx.</summary>
    public bool ShowsRealCabinet => _real.Visible && _real.Mesh != null;
    public RealCabinet? Real { get; private set; }

    /// <summary>True when this table has no VR cabinet of its own and shows the template cabinet with its own art.</summary>
    public bool Borrowed { get; private set; }
    public Vector3 PlayfieldPosition => _playfield.Position;

    Color _baseBody = Palette.SurfaceHi;
    bool _selected; // lights the backbox strip, coin lights and flipper buttons in lamp amber
    float _bright = 1f;
    const float SelectedBrightness = 1f; // the picture's own colours: past 1 it clips and blooms

    static readonly Color DarkPlayfield = Palette.Surface;
    static readonly Color DarkGlass = Palette.DmdPanel;

    public void Build(int slot, Font? displayFont, Font? dmdFont)
    {
        Slot = slot;
        AddChild(_body);
        _body.AddChild(
            new MeshInstance3D
            {
                Mesh = CabinetKit.Shadow,
                MaterialOverride = CabinetKit.ShadowMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }
        );
        Generic(new MeshInstance3D { Mesh = CabinetKit.Body, MaterialOverride = _bodyMat });
        Generic(
            new MeshInstance3D
            {
                Mesh = CabinetKit.Metal,
                MaterialOverride = CabinetKit.MetalMaterial,
            }
        );
        Generic(
            new MeshInstance3D
            {
                Mesh = CabinetKit.Chrome,
                MaterialOverride = CabinetKit.ChromeMaterial,
            }
        );
        // The generic backbox: a plain box in the side colour, the backglass picture its front (CabinetKit.BuildBackbox).
        _boxBody.MaterialOverride = _bodyMat;
        _body.AddChild(_box);
        _box.AddChild(_boxBody);
        Generic(
            new MeshInstance3D
            {
                Mesh = CabinetKit.Glow,
                MaterialOverride = _glowMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }
        );

        // Playfield: a portrait quad, image top towards the back. On the generic cabinet it lies on the
        // sloped floor of the well; on a table's own cabinet it moves to that cabinet's playfield.
        _playfield = new MeshInstance3D
        {
            Mesh = _playfieldQuad,
            MaterialOverride = _playfieldMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _body.AddChild(_playfield);
        PlaceGenericPlayfield();
        // The table's own cabinet, when it has one (see ShowReal).
        _real = new MeshInstance3D { Visible = false };
        _body.AddChild(_real);
        // The default cabinet's backbox on its own (RealCabinet.BackboxOnly), so it can be made shorter for a wide
        // picture over its whole front (ShowReal).
        _realBox = new MeshInstance3D { Visible = false };
        _body.AddChild(_realBox);
        // Faces of the default cabinet's inner side walls and back panel that a table's own pictures go on (RealCabinet.ArtFaces).
        // A table's own inner walls and back board on the default cabinet (RealCabinet.OwnWallsOn).
        _ownSides = new MeshInstance3D { Visible = false };
        _ownBack = new MeshInstance3D { Visible = false };
        _body.AddChild(_ownSides);
        _body.AddChild(_ownBack);
        _artFaces = new MeshInstance3D
        {
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _body.AddChild(_artFaces);
        // Glass over the playfield, on every cabinet (the generic one, the default one and a table's own): placed with
        // the playfield (PlacePlayfield). Barely there, and drawn after the playfield, so the depth shader under it is
        // unchanged: it only adds a sheen.
        _glass.Mesh = _glassQuad;
        _glass.MaterialOverride = CabinetKit.GlassMaterial;
        _glass.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _body.AddChild(_glass);
        // Backglass panel in the backbox (also used on a table's own backbox when its cabinet has no backglass).
        _backglassPanel = new MeshInstance3D
        {
            Mesh = _backglassQuad,
            MaterialOverride = _backglassMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        Generic(_backglassPanel);
        // An emptied backbox's front (ShowReal): a flat dark panel over its glass and speaker panel, behind the picture,
        // so a strip the picture leaves shows a plain panel rather than empty speaker hollows (World Cup Soccer).
        _frontCover = new MeshInstance3D
        {
            Mesh = _frontCoverQuad,
            MaterialOverride = FrontCoverMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        _body.AddChild(_frontCover);

        // Lit title plate, used when there's no backglass picture.
        _plate.Font = displayFont;
        _plate.FontSize = 90;
        _plate.PixelSize = 0.0011f;
        _plate.Width = CabinetKit.BackglassSize.X / _plate.PixelSize * 0.9f;
        _plate.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _plate.HorizontalAlignment = HorizontalAlignment.Center;
        _plate.OutlineSize = 0;
        _plate.Modulate = Palette.Lamp;

        _body.AddChild(_plate);
        _generic.Add(_plate);

        _star.Text = "★";
        _star.FontSize = 120;
        _star.PixelSize = 0.0012f;
        _star.OutlineSize = 14;
        _star.Modulate = Palette.Lamp;
        _star.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        _star.Position = new Vector3(0, CabinetKit.Height + 0.12f, CabinetKit.Back + 0.13f);
        _body.AddChild(_star);

        // One collider for the whole cabinet, so pointing anywhere at it selects it.
        var area = new Area3D();
        area.AddChild(
            new CollisionShape3D
            {
                Shape = new BoxShape3D
                {
                    Size = new Vector3(
                        CabinetKit.Width + 0.06f,
                        CabinetKit.Height,
                        CabinetKit.Length
                    ),
                },
                Position = new Vector3(0, CabinetKit.Height / 2, 0),
            }
        );
        area.SetMeta("cabinet_slot", slot);
        AddChild(area);

        ShowGenericBackbox(CabinetKit.DrawnBackbox); // the drawn cabinet until a table and its cabinet arrive
        Park();
    }

    void Generic(Node3D part)
    {
        _body.AddChild(part);
        _generic.Add(part);
    }

    void PlaceGenericPlayfield()
    {
        var wellZ = CabinetKit.WellCentreZ;
        _playfieldQuad.Size = CabinetKit.PlayfieldSize;
        _playfield.Rotation = new Vector3(CabinetKit.Slope - Mathf.Pi / 2, 0, 0);
        PlacePlayfield(new Vector3(0, CabinetKit.TopAt(wellZ) + 0.004f, wellZ));
        _star.Position = new Vector3(0, CabinetKit.Height + 0.12f, CabinetKit.Back + 0.13f);
    }

    /// <summary>
    /// Shows the table's own VR cabinet in place of the generic one (or goes back to generic with null).
    /// The playfield picture moves onto that cabinet's playfield, which VR cabinets keep level.
    /// A borrowed cabinet (<paramref name="borrowed"/>: the template, worn by a table on the generic cabinet) is
    /// skinned with this table's art:
    /// <list type="bullet">
    /// <item>its cabinet and backbox wear the art of the table's own VR cabinet (<paramref name="artFrom"/>) when that's
    /// built from the same cabinet kit (<see cref="RealCabinet.KitCabinetSurface"/>), so its sides, front, backbox and
    /// speaker panel are the table's, and the backglass goes in the backbox's glass;</item>
    /// <item>with no backbox art, its backbox makes way for a plain box whose whole front is the table's backglass
    /// picture (<see cref="CabinetKit.BuildBackbox"/>): such pictures usually show the whole backbox front already;</item>
    /// <item>whatever printed art is left takes the table's side colour (from its backglass).</item>
    /// </list>
    /// </summary>
    public void ShowReal(RealCabinet? real, bool borrowed = false, RealCabinet? artFrom = null)
    {
        _body.Visible = true;
        Real = real;
        Borrowed = real != null && borrowed;
        if (real == null)
        {
            _real.Visible = false;
            _realBox.Visible = false;
            _artFaces.Visible = false;
            _ownSides.Visible = _ownBack.Visible = false;
            _realBox.Mesh = null;
            _real.Mesh = null;
            foreach (var g in _generic)
            {
                g.Visible = true;
            }

            _plate.Visible = BackglassUrl == null && !HasBackglassArt;
            PlaceGenericPlayfield();
            ShowGenericBackbox(CabinetKit.DrawnBackbox);
            return;
        }
        // The table's own kit art for the template's kit parts (null where it has none, or the template lacks the part).
        // Every borrowing table gets the template's real backbox (its own kit art on it, or the side colour), in one of
        // two ways (see WholeFrontPicture): the picture in its glass over its speaker panel, or the backbox emptied
        // (speakers and display hidden) with the picture over its whole front. The plain box (CabinetKit.BuildBackbox)
        // is left only for a template without a backglass part to put a picture in.
        _artFrom = artFrom;
        _templateBox = Borrowed && real.BackglassSurface >= 0 && real.FrontOpening != null;
        _wholeFront = _templateBox && WholeFrontPicture();
        // Only a sheet laid out like the template's (RealCabinet.SameSheetLayout): one that only borrows the kit's names
        // put the wrong art on the wrong faces (Fish Tales').
        Texture2D? Sheet(Texture2D? art, int mine, int theirs) =>
            Borrowed
            && art != null
            && artFrom != null
            && RealCabinet.SameSheetLayout(real, mine, artFrom, theirs)
                ? art
                : null;
        _cabinetArtMat.AlbedoTexture = Sheet(
            artFrom?.KitCabinetArt,
            real.KitCabinetSurface,
            artFrom?.KitCabinetSurface ?? -1
        );
        _bladesArtMat.AlbedoTexture = Sheet(
            artFrom?.KitBladesArt,
            real.KitBladesSurface,
            artFrom?.KitBladesSurface ?? -1
        );
        _backboxArtMat.AlbedoTexture = _templateBox
            ? Sheet(
                artFrom?.KitBackboxArt,
                real.KitBackboxSurface,
                artFrom?.KitBackboxSurface ?? -1
            )
            : null;
        var bare = Borrowed ? real.WithoutBackbox() : null;
        // The template's backbox, drawn on its own so it can be shorter (below): with a picture over its whole front
        // that's wider than the front, the box comes down (to 75% at most) rather than the picture being squashed.
        var boxPart = Borrowed && _templateBox ? real.BackboxOnly() : null;
        if (bare == null || (_templateBox && boxPart == null))
        {
            bare = null;
            boxPart = null;
        }
        _real.Mesh = bare?.Mesh ?? real.Mesh;
        _realBox.Mesh = boxPart?.Mesh;
        _realBox.Visible = boxPart != null;
        foreach (var mi in new[] { _real, _realBox })
        {
            for (var i = 0; i < (mi.Mesh?.GetSurfaceCount() ?? 0); i++)
            {
                mi.SetSurfaceOverrideMaterial(i, null); // none left from another table
            }
        }
        var y0 = real.Backbox?.Position.Y ?? 0;
        var share =
            boxPart != null
            && _wholeFront
            && real.FrontOpening is { } op
            && PictureAspect() is { } pa
                ? Mathf.Clamp(op.Size.X / pa / op.Size.Y, 0.75f, 1f)
                : 1f;
        _realBox.Scale = new Vector3(1, share, 1);
        _realBox.Position = new Vector3(0, y0 * (1 - share), 0); // shortened from its base, which stays put
        float Lowered(float y) => y0 + (y - y0) * share;

        _real.Visible = true;
        foreach (var g in _generic)
        {
            g.Visible = false;
        }
        _box.Visible = false;
        _boxBounds = null;
        _bgTop = false;
        _frontCover.Visible = false;

        _playfieldQuad.Size = real.PlayfieldSize;
        _playfield.Rotation = new Vector3(real.PlayfieldSlope - Mathf.Pi / 2, 0, 0); // sloped like the generic one, back end up
        PlacePlayfield(real.PlayfieldCentre + new Vector3(0, 0.003f, 0));
        _star.Position = new Vector3(
            0,
            Lowered(real.Bounds.End.Y) + 0.12f,
            real.Bounds.Position.Z + 0.13f
        );

        // Backglass: the cabinet's own picture when it has one. Some VR tables set it from their script
        // (so the file has none) or draw it another way: then the game's VPS backglass fills in, on the
        // cabinet's backglass part, or as a panel on the front of its backbox.
        if (Borrowed)
        {
            HidesApron = false;
            // The table's own inner walls and back board, taken from its own VR cabinet and fitted to this playfield
            // (RealCabinet.OwnWallsOn): its real wall art, whatever it's called. A back panel picture still wins over
            // its back board.
            var (ownSides, ownBack) = artFrom?.OwnWallsOn(real) ?? (null, null);
            _ownSides.Mesh = ownSides;
            _ownSides.Visible = ownSides != null;
            _ownBack.Mesh = ownBack;
            _ownBack.Visible = ownBack != null && _backPanelMat.AlbedoTexture == null;
            DressBorrowed(_real, real, bare?.Kinds ?? real.Kinds, bare?.Sources);
            if (boxPart is { } bp)
            {
                DressBorrowed(_realBox, real, bp.Kinds, bp.Sources);
            }

            ShowArtFaces(real);
        }
        else
        {
            _artFaces.Visible = false;
            _ownSides.Visible = _ownBack.Visible = false;
        }

        // A backglass you chose yourself (UserArtwork) replaces the cabinet's own picture too.
        var mine =
            BackglassUrl?.StartsWith(
                VrTableManager.Services.UserArtwork.LocalScheme,
                StringComparison.Ordinal
            ) == true;
        if (_templateBox && !_wholeFront)
        {
            // Just the glass: in the backbox's glass part (DressBorrowed), over its speaker panel, a .directb2s
            // picture's grille cropped off.
            _backglassPanel.Visible = false;
            _plate.Visible = false;
            FitBackglass();
        }
        else if (_templateBox && real.FrontOpening is { } opening)
        {
            // A whole backbox front (or no picture yet): the backbox emptied, the picture over its front opening, at its
            // own shape, from the top (where the glass is) down.
            _bgRoom = new Vector2(opening.Size.X, opening.Size.Y * share);
            _bgCentre = new Vector3(
                opening.GetCenter().X,
                Lowered(opening.GetCenter().Y),
                real.FrontOpeningZ
            );
            _bgTop = true;
            _frontCoverQuad.Size = _bgRoom + new Vector2(0.012f, 0.012f);
            _frontCover.Position = _bgCentre - new Vector3(0, 0, 0.002f);
            _frontCover.Visible = true;
            FitBackglass();
            _backglassPanel.Visible = true;
            _plate.Visible = BackglassUrl == null && !HasBackglassArt;
        }
        else if (bare != null && real.Backbox is { } genericAt)
        {
            // A template with no backglass part to use: a plain box with the picture as its front.
            ShowGenericBackbox(genericAt);
            _backglassPanel.Visible = true;
            _plate.Visible = BackglassUrl == null && !HasBackglassArt;
        }
        else if (real.BackglassSurface >= 0 && !Borrowed && (!real.BackglassTextured || mine))
        {
            _real.SetSurfaceOverrideMaterial(real.BackglassSurface, _backglassMat);
        }
        // A cabinet with no backglass part (or a borrowed one without a backbox to replace): this table's backglass
        // goes on a panel on the backbox front.
        else if ((Borrowed || real.BackglassSurface < 0) && real.Backbox is { } box)
        {
            // The glass opening: the top 60% of the backbox (the speaker panel is below), most of its width.
            // Set into the box at its glass (RealCabinet.GlassZ), with a margin inside the frame on every side.
            _bgRoom = new Vector2(box.Size.X * 0.86f, RealCabinet.GlassHeight(box));
            _bgCentre = new Vector3(
                box.GetCenter().X,
                RealCabinet.GlassCentreY(box),
                real.GlassZ ?? box.End.Z - 0.02f
            );
            FitBackglass();
            _backglassPanel.Visible = true;
            _plate.Visible = BackglassUrl == null && !HasBackglassArt;
        }
        // No brighter material for the selected cabinet: it's lit by the spot and key lights it turns into (Main.BuildRoom).
    }

    /// <summary>True while the default cabinet wears this table's own cabinet / backbox art (tests).</summary>
    public bool WearsCabinetArt => Borrowed && _cabinetArtMat.AlbedoTexture != null;
    public bool WearsBackboxArt => Borrowed && _backboxArtMat.AlbedoTexture != null;

    // A table's own kit art on the default cabinet (see ShowReal): printed art, lit by the room like the rest.
    readonly StandardMaterial3D _cabinetArtMat = KitArtMaterial();
    readonly StandardMaterial3D _backboxArtMat = KitArtMaterial();
    readonly StandardMaterial3D _bladesArtMat = KitArtMaterial();
    readonly StandardMaterial3D _backPanelMat = KitArtMaterial();

    /// <summary>
    /// Covers a template part this table has nothing for: the apron, whose instruction cards are the template table's
    /// own (and mapped for its own picture). The playfield picture shows under where it was.
    /// </summary>
    static readonly StandardMaterial3D HiddenMaterial = new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(0, 0, 0, 0),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };

    MeshInstance3D _artFaces = null!;
    MeshInstance3D _ownSides = null!,
        _ownBack = null!;

    /// <summary>True while the default cabinet shows this table's own inner walls / back board (tests).</summary>
    public bool ShowsOwnWalls => _ownSides.Visible;
    public bool ShowsOwnBackBoard => _ownBack.Visible;
    readonly StandardMaterial3D _bladeLeftMat = KitArtMaterial();
    readonly StandardMaterial3D _bladeRightMat = KitArtMaterial();

    /// <summary>
    /// What the default cabinet's inner side walls and back panel show where a table has nothing for them: plain and
    /// dark, like a black-bladed machine, rather than art borrowed from elsewhere on the table (its backglass).
    /// </summary>
    static readonly StandardMaterial3D WallMaterial = new()
    {
        AlbedoColor = new Color(0.05f, 0.05f, 0.055f),
        Roughness = 0.55f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>The table's own inner side wall pictures (ArtLoader.VpxBladeLeftScheme / …Right), or null.</summary>
    public string? BladeLeftUrl { get; set; }
    public string? BladeRightUrl { get; set; }

    public bool HasBladeArt(bool left) =>
        (left ? _bladeLeftMat : _bladeRightMat).AlbedoTexture != null;

    /// <summary>True while the default cabinet's inner side walls show this table's own wall pictures (tests).</summary>
    public bool WearsBladeArt => _artFaces.Visible && (_shownBlades & 3) == 3;
    int _shownBlades;

    public void SetBlade(bool left, Texture2D tex)
    {
        (left ? _bladeLeftMat : _bladeRightMat).AlbedoTexture = tex;
        if (Borrowed && Real != null)
        {
            ShowArtFaces(Real);
        }
    }

    /// <summary>
    /// The table's own pictures on the default cabinet's inner side walls and back panel (RealCabinet.ArtFaces), each
    /// laid on just the face you see. The walls take them when the table brings no kit wall sheet of its own; a face
    /// with no picture is left to the plain wall under it.
    /// </summary>
    void ShowArtFaces(RealCabinet real)
    {
        var (mesh, l, r, back) = real.ArtFaces();
        _artFaces.Mesh = mesh;
        _artFaces.Visible = Borrowed;
        var kitWalls = _bladesArtMat.AlbedoTexture != null;
        _shownBlades = 0;
        void Face(int surface, StandardMaterial3D mat, bool use, int bit)
        {
            if (surface < 0)
            {
                return;
            }

            var on = use && mat.AlbedoTexture != null;
            _artFaces.SetSurfaceOverrideMaterial(surface, on ? mat : HiddenMaterial);
            _shownBlades |= on ? bit : 0;
        }
        Face(l, _bladeLeftMat, !kitWalls && !_ownSides.Visible, 1);
        Face(r, _bladeRightMat, !kitWalls && !_ownSides.Visible, 2);
        Face(back, BackPanelArt(real), !_ownBack.Visible, 4);
    }

    readonly StandardMaterial3D _backFromSidesMat = KitArtMaterial();

    /// <summary>
    /// What the default cabinet's back panel shows: the table's own back panel picture; else its side wall art carried
    /// round the back, as on many real machines (most tables have no back panel picture, and a kit wall sheet leaves
    /// its back piece black): its own wall strip, or the left wall's part of its kit wall sheet, turned to lie along
    /// the panel. With none of those, the plain board.
    /// </summary>
    StandardMaterial3D BackPanelArt(RealCabinet real)
    {
        if (_backPanelMat.AlbedoTexture != null)
        {
            return _backPanelMat;
        }

        _backFromSidesMat.AlbedoTexture =
            _bladeLeftMat.AlbedoTexture
            ?? _bladeRightMat.AlbedoTexture
            ?? (
                _bladesArtMat.AlbedoTexture is { } sheet
                && real.FaceUv(real.KitBladesSurface, 1) is { } wall
                    ? SideWallStrip(sheet, wall)
                    : null
            );
        return _backFromSidesMat;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Texture2D,
        Texture2D
    > SideStrips = [];

    /// <summary>
    /// The part of a kit wall sheet one wall uses (<paramref name="wall"/>, in texture coordinates), cut out and turned
    /// so the wall's length runs across: a picture for the back panel. Made once per sheet.
    /// </summary>
    static Texture2D? SideWallStrip(Texture2D sheet, Rect2 wall)
    {
        if (SideStrips.TryGetValue(sheet, out var made))
        {
            return made;
        }

        try
        {
            var img = sheet.GetImage();
            if (img == null)
            {
                return null;
            }

            if (img.IsCompressed())
            {
                img.Decompress();
            }

            int w = img.GetWidth(),
                h = img.GetHeight();
            var r = new Rect2I(
                (int)(wall.Position.X * w),
                (int)(wall.Position.Y * h),
                Math.Max(1, (int)(wall.Size.X * w)),
                Math.Max(1, (int)(wall.Size.Y * h))
            ).Intersection(new Rect2I(0, 0, w, h));
            var strip = img.GetRegion(r);
            if (strip.HasMipmaps())
            {
                strip.ClearMipmaps();
            }

            if (strip.GetHeight() > strip.GetWidth())
            {
                strip.Rotate90(ClockDirection.Counterclockwise);
            }
            ArtLoader.Shrink(strip, 1024);
            strip.GenerateMipmaps();
            made = ImageTexture.CreateFromImage(strip);
            SideStrips.AddOrUpdate(sheet, made);
            return made;
        }
        catch (Exception e)
        {
            GD.PushWarning($"Couldn't cut a side wall strip for the back panel: {e.Message}");
            return null;
        }
    }

    /// <summary>The table's own back panel picture (ArtLoader.VpxBackPanelScheme), or null: the default cabinet's back panel wears it.</summary>
    public string? BackPanelUrl { get; set; }
    public bool HasBackPanelArt => _backPanelMat.AlbedoTexture != null;

    /// <summary>True while the default cabinet's back panel wears this table's picture (tests).</summary>
    public bool WearsBackPanelArt => Borrowed && Real?.BackPanelSurface >= 0 && HasBackPanelArt;

    public void SetBackPanel(Texture2D tex)
    {
        _backPanelMat.AlbedoTexture = tex;
        if (Borrowed && Real != null)
        {
            ShowReal(Real, borrowed: true, artFrom: _artFrom); // its own back board may give way to the picture
        }
    }

    RealCabinet? _artFrom;

    static StandardMaterial3D KitArtMaterial() =>
        new()
        {
            Roughness = 0.6f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // like every VR cabinet material (see _bodyMat)
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };

    static StandardMaterial3D? _dmdPanelMat;
    static StandardMaterial3D DmdPanelMaterial =>
        _dmdPanelMat ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = Palette.DmdPanel,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // stands in for a VR cabinet part: both sides (see _bodyMat)
        };

    /// <summary>
    /// The template cabinet (e.g. Medieval Madness) dressed for this table: its kit parts wear the table's own art when
    /// it has some (see ShowReal), and the backglass part this table's backglass with it; other printed art becomes
    /// this table's side colour (from its backglass), its speaker grilles plain grille, its display a dark panel.
    /// Metal, chrome, legs and the coin door stay as they are. <paramref name="sources"/>: each shown surface's index in
    /// the template's own mesh, when its backbox was taken out (RealCabinet.WithoutBackbox).
    /// </summary>
    /// <summary>True while the default cabinet hides its template's apron (tests).</summary>
    public bool HidesApron { get; private set; }

    void DressBorrowed(
        MeshInstance3D target,
        RealCabinet real,
        IReadOnlyList<SurfaceKind> kinds,
        IReadOnlyList<int>? sources
    )
    {
        for (var i = 0; i < kinds.Count; i++)
        {
            var from = sources?[i] ?? i;
            Material? m =
                from == real.KitCabinetSurface && _cabinetArtMat.AlbedoTexture != null
                    ? _cabinetArtMat
                : from == real.ApronSurface ? HiddenMaterial
                // The template's back board: plain, its front face carrying the table's picture (ShowArtFaces); out of
                // the way when the table's own back board stands there instead (OwnWallsOn).
                : from == real.BackPanelSurface ? (_ownBack.Visible ? HiddenMaterial : WallMaterial)
                // The template's walls: out of the way of the table's own (OwnWallsOn), else its kit wall sheet, else plain
                // under the table's own wall strips (ShowArtFaces).
                : from == real.KitBladesSurface && _ownSides.Visible ? HiddenMaterial
                : from == real.KitBladesSurface && _bladesArtMat.AlbedoTexture != null
                    ? _bladesArtMat
                : from == real.KitBladesSurface ? WallMaterial
                : from == real.KitBackboxSurface && _backboxArtMat.AlbedoTexture != null
                    ? _backboxArtMat
                : from == real.BackglassSurface && _templateBox && !_wholeFront ? _backglassMat
                : kinds[i] switch
                {
                    SurfaceKind.Art => _bodyMat,
                    // An emptied backbox: no speakers or display (the picture over its front shows its own).
                    SurfaceKind.Grille or SurfaceKind.Dmd when _wholeFront => HiddenMaterial,
                    SurfaceKind.Grille => CabinetKit.GrilleMaterial,
                    SurfaceKind.Dmd => DmdPanelMaterial,
                    SurfaceKind.Backglass => DmdPanelMaterial,
                    _ => null,
                };
            target.SetSurfaceOverrideMaterial(i, m);
            HidesApron |= from == real.ApronSurface;
        }
    }

    /// <summary>Puts the generic backbox at <paramref name="at"/> and shapes it to the backglass (see <see cref="FitBackglass"/>).</summary>
    void ShowGenericBackbox(Aabb at)
    {
        _boxBounds = at;
        _bgTop = false;
        _frontCover.Visible = false;
        _box.Visible = true;
        FitBackglass();
    }

    /// <summary>Room for the backglass panel on a table's own backbox, and its centre; see <see cref="FitBackglass"/>.</summary>
    Vector2 _bgRoom = CabinetKit.BackglassSize;

    MeshInstance3D _frontCover = null!;
    readonly QuadMesh _frontCoverQuad = new();

    static readonly StandardMaterial3D FrontCoverMaterial = new()
    {
        AlbedoColor = new Color(0.035f, 0.035f, 0.04f),
        Roughness = 0.7f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>Fit the picture to the top of its room rather than its middle (a whole front: the glass is at the top).</summary>
    bool _bgTop;

    /// <summary>The default cabinet shows its template's own backbox, and whether that's emptied for a whole-front picture.</summary>
    bool _templateBox,
        _wholeFront;

    /// <summary>True while the default cabinet's backbox is emptied (speakers and display hidden) with the picture over its whole front (tests).</summary>
    public bool ShowsWholeFrontPicture => Borrowed && _wholeFront;

    /// <summary>The backglass picture's shape (width per height, all of it), or null before it arrives.</summary>
    float? PictureAspect() =>
        _backglassMat.AlbedoTexture is { } t && t.GetHeight() > 0
            ? (float)t.GetWidth() / t.GetHeight()
            : null;

    /// <summary>
    /// Is the backglass picture a whole backbox front (glass, speakers and display), rather than just the glass? The
    /// catalog's often is (a photo of the machine): it counts as a whole front. So does a picture nearly as tall as
    /// wide (under 1.18 wide per tall, after a .directb2s's marked grille): a glass with a speaker panel under it, or an
    /// old machine's tall glass (an EM's backbox has no speaker panel either). A landscape glass from a .directb2s
    /// (Masters of the Universe's marks no grille), the table file or you goes in the glass over the speakers.
    /// A glass-only picture that hasn't arrived yet counts as glass only (most are), so the speakers show meanwhile;
    /// if it then falls back to the catalog's picture, the backbox turns to a whole front then.
    /// </summary>
    bool WholeFrontPicture()
    {
        if (BackglassUrl == null || !ArtLoader.IsGlassOnly(BackglassUrl))
        {
            return true;
        }

        if (_backglassMat.AlbedoTexture is not { } tex || tex.GetHeight() == 0)
        {
            return false;
        }

        var aspect = tex.GetWidth() / (tex.GetHeight() * (1f - _grilleShare));
        return aspect < 1.18f;
    }

    Vector3 _bgCentre;

    /// <summary>
    /// Sizes the backglass panel to its picture. On the generic backbox, the box is shaped to the picture and the whole
    /// picture is its front, speaker grille and all (see <see cref="CabinetKit.BuildBackbox"/>). On a table's own
    /// backbox, which has its own speaker panel, a .directb2s picture's grille is cropped off (with the material's UVs)
    /// and the glass is as big as the opening allows, centred. It used to be 4:3 whatever the picture, so a wider or
    /// taller backglass was squeezed, and on a table's own backbox it left strips of the box showing beside it
    /// (Beavis and Butt-Head, Terminator 3, The Addams Family).
    /// </summary>
    void FitBackglass()
    {
        // The glass only (a .directb2s grille cut off) where it goes over a speaker panel; all of it on a whole front.
        var keep = _boxBounds == null && !_wholeFront ? 1f - _grilleShare : 1f;
        _backglassMat.Uv1Scale = new Vector3(1, keep, 1);
        float? aspect =
            _backglassMat.AlbedoTexture is { } tex && tex.GetHeight() > 0
                ? tex.GetWidth() / (tex.GetHeight() * keep)
                : null;
        Vector2 size;
        if (_boxBounds is { } at)
        {
            var box = CabinetKit.BuildBackbox(at, aspect);
            _boxBody.Mesh = box.Body;
            _bgCentre = box.GlassCentre;
            size = box.GlassSize;
            BackboxBounds = box.Bounds;
            _star.Position = new Vector3(
                _star.Position.X,
                box.Bounds.End.Y + 0.12f,
                _star.Position.Z
            );
        }
        else
        {
            BackboxBounds = null;
            size = _bgRoom;
            // A whole backbox front is stretched to fill the emptied front, rather than leave a dark band (Fish Tales).
            if (aspect is { } a && !(Borrowed && _wholeFront))
            {
                size =
                    a >= _bgRoom.X / _bgRoom.Y
                        ? new Vector2(_bgRoom.X, _bgRoom.X / a)
                        : new Vector2(_bgRoom.Y * a, _bgRoom.Y);
            }
        }
        _backglassQuad.Size = size;
        _backglassPanel.Position = _bgTop
            ? _bgCentre + new Vector3(0, (_bgRoom.Y - size.Y) / 2, 0)
            : _bgCentre;
        _plate.Position = _backglassPanel.Position + new Vector3(0, 0, 0.004f);
        _plate.Width = size.X / _plate.PixelSize * 0.9f;
    }

    /// <summary>Shows a table on this cabinet. Art arrives later through SetPlayfield / SetBackglass.</summary>
    public void Assign(
        int index,
        LobbyTable table,
        string? playfieldUrl,
        string? backglassUrl,
        bool favourite
    )
    {
        Visible = true;
        var same = Table == table && Index == index;
        Index = index;
        Table = table;
        _star.Visible = favourite;
        var samePlayfield = same && playfieldUrl == PlayfieldUrl;
        if (samePlayfield && backglassUrl == BackglassUrl)
        {
            return;
        }

        PlayfieldUrl = playfieldUrl;
        BackglassUrl = backglassUrl;
        // Only the backglass changed (the VPS backglass index arrived): the playfield keeps its picture, and a lobby
        // video playing on it (its texture is only handed over once, on the first frame) keeps playing.
        if (!samePlayfield)
        {
            _playfieldMat.AlbedoTexture = _playfieldArt = null;
        }
        _backglassMat.AlbedoTexture = null;
        _backPanelMat.AlbedoTexture = null; // the new table's, if it has one, arrives through SetBackPanel
        _bladeLeftMat.AlbedoTexture = _bladeRightMat.AlbedoTexture = null; // likewise, through SetBlade
        _plate.Text = table.Title;
        _grilleShare = 0;
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
        var playingVideo =
            _playfieldMat.AlbedoTexture != null && _playfieldMat.AlbedoTexture != _playfieldArt;
        _playfieldArt = tex;
        if (!playingVideo)
        {
            _playfieldMat.AlbedoTexture = tex;
        }

        ApplyColours();
    }

    /// <summary>Shows a lobby video frame texture on the playfield, or (null) goes back to the still picture.</summary>
    public void SetPlayfieldVideo(Texture2D? tex)
    {
        _playfieldMat.AlbedoTexture = tex ?? _playfieldArt;
        ApplyColours();
    }

    /// <summary>
    /// Shows the playfield with depth from the table's height map (<see cref="PlayfieldDepth"/>), or flat again
    /// with null. The quad stays on the playfield; the shader traces each eye's ray from <paramref name="depth"/>
    /// metres above it (glass level) down to it, so ramps and toys stand up without anything being lifted.
    /// </summary>
    public void SetPlayfieldDepth(Texture2D? heights, float depth = 0)
    {
        if (heights == null)
        {
            _depthMat = null;
            _playfield.MaterialOverride = _playfieldMat;
        }
        else
        {
            _depthMat ??= new ShaderMaterial { Shader = PlayfieldDepth.Shader };
            _depthMat.SetShaderParameter("height_tex", heights);
            _depthMat.SetShaderParameter("depth", depth);
            _depthMat.SetShaderParameter("top", VpxCabinet.HeightMapTop * VpxCabinet.VpuToMetres);
            _depthMat.SetShaderParameter("size", _playfieldQuad.Size);
            _playfield.MaterialOverride = _depthMat;
        }
        ApplyColours();
    }

    void PlacePlayfield(Vector3 onPlayfield)
    {
        _playfield.Position = onPlayfield;
        _depthMat?.SetShaderParameter("size", _playfieldQuad.Size);
        // The glass, the playfield's size and slope, at about a real machine's height over it.
        _glassQuad.Size = _playfieldQuad.Size;
        _glass.Rotation = _playfield.Rotation;
        _glass.Position =
            onPlayfield + new Vector3(0, Real == null ? CabinetKit.GlassLift : RealGlassLift, 0);
        // On a VR cabinet: just under the top of its side rails, along their line (RealCabinet.RailTops), over the
        // same stretch of the playfield.
        if (Real?.RailTops is var (front, back))
        {
            var slope = Mathf.Atan2(back.Y - front.Y, front.X - back.X);
            var run = _playfieldQuad.Size.Y * Mathf.Cos(_playfield.Rotation.X + Mathf.Pi / 2);
            var z = onPlayfield.Z;
            var y = front.Y + (front.X - z) * Mathf.Tan(slope) - 0.006f;
            // A little past each end, under the lockdown bar and the back panel, so it never stops short of either.
            _glassQuad.Size = new Vector2(_playfieldQuad.Size.X, (run + 0.08f) / Mathf.Cos(slope));
            _glass.Rotation = new Vector3(slope - Mathf.Pi / 2, 0, 0);
            _glass.Position = new Vector3(onPlayfield.X, y, z);
        }
    }

    readonly MeshInstance3D _glass = new();
    readonly QuadMesh _glassQuad = new();

    /// <summary>Glass height over the playfield on a VR cabinet: about a real machine's, just under its side rails.</summary>
    const float RealGlassLift = 0.08f;

    /// <summary>The glass over the playfield: centre and size (tests).</summary>
    public (Vector3 Centre, Vector2 Size, bool Visible) Glass =>
        (_glass.Position, _glassQuad.Size, _glass.Visible);

    /// <summary>The backglass also gives the cabinet its side colour (a darkened average of the art).</summary>
    public void SetBackglass(Texture2D tex, Color average)
    {
        _backglassMat.AlbedoTexture = tex;
        _grilleShare = ArtLoader.GrilleShare(BackglassUrl);
        // On the default cabinet, whether the picture is just the glass decides the backbox (see ShowReal): it may have
        // come from the catalog after all (its .directb2s or table file had none).
        if (Borrowed && Real != null)
        {
            ShowReal(Real, borrowed: true, artFrom: _artFrom);
        }
        FitBackglass();
        _plate.Visible = false;
        _baseBody = SideColour(average);
        ApplyColours();
    }

    public void SetFavourite(bool on) => _star.Visible = on;

    /// <summary>1 for the selected cabinet, fading to ~0.35 for the ones around the ring.</summary>
    public void SetFocus(float focus, bool selected)
    {
        // The playfield and backglass are self-lit (unshaded): the one in front of you shows the picture at its own
        // brightness, and the others dim around the ring.
        _bright = Mathf.Lerp(0.35f, SelectedBrightness, Mathf.Clamp(focus, 0f, 1f));
        _selected = selected;
        ApplyColours();
    }

    void ApplyColours()
    {
        var b = _bright;
        // Shaded parts keep their own colour: they're darker around the ring because the spot and key lights don't reach there.
        _bodyMat.AlbedoColor = _baseBody;
        _playfieldMat.AlbedoColor =
            (_playfieldMat.AlbedoTexture == null ? DarkPlayfield : Colors.White)
            * new Color(b, b, b);
        if (_depthMat != null)
        {
            _depthMat.SetShaderParameter("albedo_tex", _playfieldMat.AlbedoTexture);
            _depthMat.SetShaderParameter("tint", _playfieldMat.AlbedoColor);
        }
        _backglassMat.AlbedoColor =
            (_backglassMat.AlbedoTexture == null ? DarkGlass : Colors.White) * new Color(b, b, b);
        // The selected cabinet lights up in lamp amber; the others glow in their table type's insert colour.
        var lamp =
            Table == null ? Palette.Blue
            : _selected ? Palette.Lamp
            : Palette.Insert(Table.Badge);
        _glowMat.AlbedoColor = lamp;
        _glowMat.Emission = lamp;
        _glowMat.EmissionEnergyMultiplier = 0.3f + 0.9f * Mathf.Min(b - 0.35f, 0.65f) / 0.65f;
        _plate.Modulate = new Color(Palette.Lamp, Mathf.Min(b, 1f));
    }

    static Color SideColour(Color average)
    {
        // Keep the hue, but dark and a little saturated, like printed side art in a dim room.
        average.ToHsv(out var h, out var s, out var v);
        return Color.FromHsv(
            h,
            Mathf.Clamp(s * 1.1f, 0.3f, 0.85f),
            Mathf.Clamp(v * 0.6f, 0.16f, 0.42f)
        );
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
    public static Color Insert(string badge) =>
        badge switch
        {
            "FP" => Red,
            "FX" or "M" => Green,
            _ => Blue,
        };
}
