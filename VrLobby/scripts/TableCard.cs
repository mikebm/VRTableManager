using Godot;
using VrTableManager.Lobby;

namespace VrLobby;

/// <summary>One table in the ring or wall: artwork, a glowing frame when selected, title, engine badge and favourite star.</summary>
public partial class TableCard : Node3D
{
    public static readonly Vector2 Size = new(1.0f, 0.62f);

    public int Index { get; private set; } = -1;
    public LobbyTable Table { get; private set; } = null!;

    readonly StandardMaterial3D _artMat = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
    readonly StandardMaterial3D _frameMat = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Idle };
    readonly Label3D _title = new();
    readonly Label3D _fav = new();

    static readonly Color Idle = new(0.12f, 0.12f, 0.16f);
    static readonly Color Gold = new(1.0f, 0.78f, 0.25f);

    public void Setup(int index, LobbyTable table)
    {
        Index = index;
        Table = table;

        AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Size + new Vector2(0.06f, 0.06f) },
            MaterialOverride = _frameMat,
            Position = new Vector3(0, 0, -0.005f),
        });

        // Placeholder look until the artwork arrives: a colour derived from the title.
        // (string.GetHashCode is randomised per run in .NET, so use a stable one.)
        var hue = table.Title.Aggregate(17u, (h, c) => h * 31 + c) % 1000 / 1000f;
        _artMat.AlbedoColor = Color.FromHsv(hue, 0.45f, 0.35f);
        AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = Size }, MaterialOverride = _artMat });

        _title.Text = table.Title;
        _title.FontSize = 48;
        _title.PixelSize = 0.0012f;
        _title.Width = 820;
        _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _title.VerticalAlignment = VerticalAlignment.Top;
        _title.Position = new Vector3(0, -Size.Y / 2 - 0.05f, 0.01f);
        _title.OutlineSize = 12;
        AddChild(_title);

        AddChild(new Label3D
        {
            Text = table.Badge,
            FontSize = 36,
            PixelSize = 0.0012f,
            OutlineSize = 10,
            Modulate = table.Badge switch
            {
                "VPX" => new Color(0.55f, 0.75f, 1f),     // blue
                "FP" => new Color(1f, 0.75f, 0.4f),       // orange
                "FX" => new Color(0.75f, 1f, 0.6f),       // green
                "M" => new Color(0.95f, 0.5f, 1f),        // magenta (Pinball M)
                _ => Colors.White,
            },
            Position = new Vector3(Size.X / 2 - 0.07f, Size.Y / 2 - 0.05f, 0.01f),
        });

        _fav.Text = "★ FAV";
        _fav.FontSize = 36;
        _fav.PixelSize = 0.0012f;
        _fav.OutlineSize = 10;
        _fav.Modulate = new Color(1f, 0.82f, 0.2f);
        _fav.Position = new Vector3(-Size.X / 2 + 0.1f, Size.Y / 2 - 0.05f, 0.01f);
        _fav.Visible = false;
        AddChild(_fav);

        // Collider so the controller ray can point at cards.
        var area = new Area3D();
        area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(Size.X, Size.Y, 0.02f) } });
        area.SetMeta("card_index", index);
        AddChild(area);
    }

    public void SetTexture(Texture2D tex)
    {
        _artMat.AlbedoTexture = tex;
        _artMat.AlbedoColor = Colors.White;
    }

    public void SetFavourite(bool on) => _fav.Visible = on;
    public bool ShowsFavourite => _fav.Visible;

    public void SetSelected(bool selected)
    {
        _frameMat.AlbedoColor = selected ? Gold : Idle;
        _title.Modulate = selected ? Colors.White : new Color(1, 1, 1, 0.6f);
    }
}
