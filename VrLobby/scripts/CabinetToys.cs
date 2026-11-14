using Godot;

namespace VrLobby;

// A table's own 3D toys on this cabinet's playfield (TableToys), for the selected cabinet only.
public partial class Cabinet
{
    MeshInstance3D? _toys;

    /// <summary>The height map the playfield video last handed over, held back while toys are shown (SetPlayfieldDepth).</summary>
    Texture2D? _heldHeights;
    float _heldDepth;

    public TableToys? Toys { get; private set; }
    public bool ShowsToys => _toys is { Visible: true } && Toys != null;

    /// <summary>
    /// Shows a table's toys standing on this cabinet's playfield, whichever cabinet it is (its own, or the default one
    /// it borrows: then they're stretched to that playfield, as its video is), or hides them with null. While they're
    /// shown the playfield video lies flat: its depth would raise a second copy of every toy under them.
    /// </summary>
    public void ShowToys(TableToys? toys)
    {
        if (toys == null)
        {
            if (_toys != null)
            {
                _toys.Visible = false;
                _toys.Mesh = null;
            }
            if (Toys != null)
            {
                Toys = null;
                SetPlayfieldDepth(_heldHeights, _heldDepth);
            }
            return;
        }
        if (_toys == null)
        {
            _toys = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _body.AddChild(_toys);
        }
        var changed = Toys != toys;
        Toys = toys;
        _toys.Mesh = toys.Mesh;
        _toys.Visible = true;
        PlaceToys();
        if (changed)
        {
            SetPlayfieldDepth(_heldHeights, _heldDepth);
        }
    }

    /// <summary>Puts the toys on the playfield where it is now (the cabinet under them can change: loading, own, borrowed).</summary>
    public void PlaceToys()
    {
        if (_toys == null || Toys == null)
        {
            return;
        }

        var size = Toys.PlayfieldSize;
        var quad = _playfieldQuad.Size;
        float sx = quad.X / Mathf.Max(size.X, 1e-3f),
            sz = quad.Y / Mathf.Max(size.Y, 1e-3f);
        // The quad lies at its slope - 90° (picture top towards the back); the toys stand up from it at its slope.
        var slope = _playfield.Rotation.X + Mathf.Pi / 2;
        _toys.Transform = new Transform3D(
            new Basis(Vector3.Right, slope) * Basis.FromScale(new Vector3(sx, (sx + sz) / 2, sz)),
            _playfield.Position
        );
    }
}
