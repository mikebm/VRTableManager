using VrTableManager.Models;
using VrTableManager.Services;

namespace VrTableManager.ViewModels;

// Get Tables → INSTALL on a VPU Remix: the download is a .dif patch for another table, so the remix is made from that
// table. Its base is found among your tables (or installed first), and you choose whether the remix replaces it or
// goes in as a table of its own.
public sealed partial class CatalogViewModel
{
    const string NewTableButton = "INSTALL AS A NEW TABLE";
    const string CancelButton = "CANCEL";

    /// <summary>The installed tables a remix's patch may apply to, best first (see <see cref="VpuRemix.BaseCandidates"/>).</summary>
    internal List<VpuRemix.BaseCandidate> RemixBases(VpsGame game, VpsTableFile tf) =>
        VpuRemix.BaseCandidates(
            tf,
            game.Id,
            App.Library._allInstalled.Select(i => i.Table),
            VpuRemix.BaseOf(tf, _allGames, game)?.Game.Id
        );

    /// <summary>
    /// With no exact base and several tables the patch may fit, asks which one the remix is made from; that one comes
    /// first (it's the one a REPLACE puts the remix in place of). Null when cancelled.
    /// </summary>
    internal async Task<List<VpuRemix.BaseCandidate>?> PickRemixBaseAsync(
        string title,
        List<VpuRemix.BaseCandidate> bases
    )
    {
        if (bases.Count < 2 || bases[0].Exact)
        {
            return bases;
        }

        var shown = bases.Take(3).ToList();
        var pick = await Ui.ChoiceAsync(
            title,
            "Which of your tables is the remix made from? The catalog doesn't say; its page or ReadMe names the "
                + "original.",
            [
                .. shown.Select(b =>
                    Path.GetFileNameWithoutExtension(b.Table.TablePath).ToUpperInvariant()
                ),
                CancelButton,
            ]
        );
        if (pick < 0 || pick >= shown.Count)
        {
            return null;
        }

        var chosen = shown[pick];
        return [chosen, .. bases.Where(b => b != chosen)];
    }

    async Task<bool> InstallRemixAsync(VpsGame game, VpsTableFile tf)
    {
        var parent = VpuRemix.BaseOf(tf, _allGames, game);
        if (parent?.File.Id == tf.Id)
        {
            parent = null; // names itself: as good as not saying
        }

        var bases = RemixBases(game, tf);
        var title = $"{game.Name}: VPU Remix";
        if (bases.Count == 0)
        {
            if (parent is not { } p)
            {
                // VPS doesn't say which table it patches: only its page (ReadMe) does.
                var url = tf.Urls?.FirstOrDefault(u => !u.Broken)?.Url;
                var pick = await Ui.ChoiceAsync(
                    title,
                    "This remix is a patch for another table, not a table, and the catalog doesn't say which one. "
                        + "Its page or ReadMe names the original table: install that exact version first, then "
                        + "install the remix again and it's made from it.",
                    url == null ? ["OK"] : ["OPEN PAGE", CancelButton]
                );
                if (pick == 0 && url != null)
                {
                    Ui.OpenExternal(url);
                }

                return false;
            }

            var both = await Ui.ChoiceAsync(
                title,
                $"This remix is a patch for {p.Game.Name} {VpuRemix.Describe(p.File)}, not a table. That table "
                    + "isn't installed: install it first, then the remix is made from it?\n\n"
                    + "The patch only fits the exact version it was made from. If the catalog's version has moved on "
                    + "since, the remix can't be made and nothing is installed (the table stays).",
                ["INSTALL BOTH", CancelButton]
            );
            // As a plain table: a parent that is itself a remix (or names the remix back) can't loop the dialogs.
            if (both != 0 || !await InstallNewAsync(p.Game, p.File))
            {
                return false;
            }

            bases = RemixBases(game, tf);
            if (bases.Count == 0)
            {
                CatalogStatus =
                    $"{p.Game.Name} was installed, but isn't listed yet: install the remix again.";
                return false;
            }
        }

        if (await PickRemixBaseAsync(title, bases) is not { } ordered)
        {
            return false;
        }

        return await NewOrReplaceAsync(game, tf, parent, ordered[0], null);
    }

    /// <summary>
    /// My Tables → MORE → VPU Remixes → a catalog remix of that table: made from that table only, whatever the
    /// catalog says its base is.
    /// </summary>
    internal async Task<bool> InstallRemixOntoAsync(
        VpsGame game,
        VpsTableFile tf,
        InstalledTable onto
    )
    {
        if (VpuRemix.CandidateFor(onto, tf) is not { } candidate)
        {
            CatalogStatus =
                $"{onto.Manifest.GameName}: the table file to patch is gone (rescan My Tables, or install the "
                + "original again from Get Tables).";
            return false;
        }

        var parent = VpuRemix.BaseOf(tf, _allGames, game);
        if (parent?.File.Id == tf.Id)
        {
            parent = null;
        }

        return await NewOrReplaceAsync(game, tf, parent, candidate, [candidate.SourcePath]);
    }

    /// <summary>
    /// The table a remix is patched from, as its manifest was: the kept original, for a remix in its place.
    /// </summary>
    internal static TableManifest BaseManifest(InstalledTable t) =>
        VpuRemix.KeptOriginal(t) != null ? t.Manifest.RemixOriginal!.Manifest : t.Manifest;

    /// <summary>
    /// Asks whether the remix goes in as a new table or in place of <paramref name="first"/>, then installs it.
    /// <paramref name="only"/>: the files to patch (null: the remix's bases among your tables, read when it's
    /// downloaded).
    /// </summary>
    async Task<bool> NewOrReplaceAsync(
        VpsGame game,
        VpsTableFile tf,
        (VpsGame Game, VpsTableFile File)? parent,
        VpuRemix.BaseCandidate first,
        IReadOnlyList<string>? only
    )
    {
        var title = $"{game.Name}: VPU Remix";
        var made = BaseManifest(first.Table);
        var baseName = first.Table.Manifest.GameName;
        var warn = "";
        if (!first.Exact)
        {
            warn = parent is { } q
                ? $"\n\nYour {baseName} isn't the table the remix patches ({VpuRemix.Describe(q.File)}). It's "
                    + "tried anyway; if the patch doesn't fit, nothing is installed."
                : "";
        }
        else if (
            parent is { } q
            && !string.IsNullOrWhiteSpace(q.File.Version)
            && !TableUpdates.SameVersion(q.File.Version, made.Version)
        )
        {
            warn =
                $"\n\nYou have v{made.Version ?? "?"}; the catalog now lists v{q.File.Version}. "
                + "The patch fits only the version it was made from; if it doesn't fit, nothing is installed.";
        }

        var from =
            VpuRemix.KeptOriginal(first.Table) != null
                ? $"the original {made.GameName} kept beside your {baseName} remix"
                : $"your {baseName} ({Path.GetFileName(first.Table.TablePath)})";
        var choice = await AskNewOrReplaceAsync(
            title,
            $"The remix is made by patching {from}.",
            first.Table,
            warn
        );
        switch (choice)
        {
            case 0:
                return await InstallNewAsync(game, tf, only);
            case 1:
                return await InstallAsync(game, tf, first.Table, only ?? [first.SourcePath]);
            default:
                return false;
        }
    }

    /// <summary>
    /// Asks whether a remix goes in as a new table or in place of <paramref name="onto"/>: 0 new, 1 replace, -1
    /// cancelled. What replacing does depends on the table: an original is kept beside the remix; a remix installed as
    /// a table of its own is just replaced; a table outside a folder of its own can't be replaced at all.
    /// </summary>
    internal async Task<int> AskNewOrReplaceAsync(
        string title,
        string intro,
        InstalledTable onto,
        string warn = ""
    )
    {
        var name = onto.Manifest.GameName;
        if (!TableInstaller.CanKeepOriginal(onto, Settings))
        {
            var only = await Ui.ChoiceAsync(
                title,
                $"{intro}\n\nIt goes in as a new table next to your {name}: that table isn't in a folder of its "
                    + "own, so the remix can't take its place and keep it."
                    + warn,
                [NewTableButton, CancelButton]
            );
            return only == 0 ? 0 : -1;
        }

        var standAlone = VpuRemix.IsRemix(onto.Manifest) && onto.Manifest.RemixOriginal == null;
        var replacing = standAlone
            ? $"Your {name} is itself a remix: replacing it removes it (it isn't kept). The table's folder, ROMs, "
                + "high scores, settings and play stats stay."
            : "Replacing keeps the table's folder, ROMs, high scores, settings and play stats, and keeps the "
                + "original beside it: MORE → VPU Remixes on its card puts it back.";
        var pick = await Ui.ChoiceAsync(
            title,
            $"{intro}\n\nInstall it as a new table next to it, or replace it? {replacing}{warn}",
            [NewTableButton, $"REPLACE {name.ToUpperInvariant()}", CancelButton]
        );
        return pick is 0 or 1 ? pick : -1;
    }

    // ---- My Tables → MORE → VPU Remixes: the catalog's remixes of a table ----

    Dictionary<string, List<(VpsGame Game, VpsTableFile File)>> _remixesByParent = [];
    Dictionary<string, List<(VpsGame Game, VpsTableFile File)>> _remixesByGame = [];
    List<VpsGame>? _remixIndexFor;

    /// <summary>The catalog's remixes of this table, made from it first, then newest first; none for other engines.</summary>
    internal IReadOnlyList<RemixChoice> RemixChoicesFor(InstalledItem item)
    {
        var t = item.Table;
        if (!t.IsVpx || _allGames.Count == 0)
        {
            return [];
        }

        if (!ReferenceEquals(_remixIndexFor, _allGames))
        {
            _remixesByParent = [];
            _remixesByGame = [];
            foreach (var g in _allGames)
            {
                foreach (var f in g.TableFiles ?? [])
                {
                    if (!VpsCatalog.IsVpx(f) || !VpuRemix.IsRemix(f))
                    {
                        continue;
                    }

                    if (VpuRemix.ParentId(f) is { } p)
                    {
                        Add(_remixesByParent, p, (g, f));
                    }
                    Add(_remixesByGame, g.Id, (g, f));
                }
            }
            _remixIndexFor = _allGames;
        }

        var id = t.Manifest.RemixOriginal?.Manifest ?? t.Manifest;
        var found = new Dictionary<string, (VpsGame Game, VpsTableFile File, bool Exact)>();
        if (id.TableFileId != "" && _remixesByParent.TryGetValue(id.TableFileId, out var children))
        {
            foreach (var (g, f) in children)
            {
                found.TryAdd(f.Id, (g, f, true));
            }
        }
        if (id.GameId != "" && _remixesByGame.TryGetValue(id.GameId, out var same))
        {
            foreach (var (g, f) in same)
            {
                found.TryAdd(f.Id, (g, f, false));
            }
        }
        found.Remove(t.Manifest.TableFileId); // the remix this table is now

        return found
            .Values.OrderByDescending(r => r.Exact)
            .ThenByDescending(r => r.File.UpdatedAt ?? 0)
            .Select(r => new RemixChoice(item, r.Game, r.File, Label(r.Game, r.File, r.Exact)))
            .ToList();

        string Label(VpsGame g, VpsTableFile f, bool exact)
        {
            var title = VpuRemix.Title(f);
            if (exact)
            {
                return title;
            }

            // Made from another version: say which, so it's no surprise when it doesn't fit.
            var parent = VpuRemix.ParentId(f) is { } p
                ? g.TableFiles?.FirstOrDefault(x => x.Id == p)
                : null;
            return parent?.Version is { Length: > 0 } v
                ? $"{title} (made from v{v.Trim().TrimStart('v', 'V')})"
                : $"{title} (made from another version)";
        }

        static void Add(
            Dictionary<string, List<(VpsGame, VpsTableFile)>> d,
            string key,
            (VpsGame, VpsTableFile) v
        )
        {
            if (!d.TryGetValue(key, out var l))
            {
                d[key] = l = [];
            }
            l.Add(v);
        }
    }
}

/// <summary>My Tables → MORE → VPU Remixes: one of the catalog's remixes of the card's table.</summary>
public sealed record RemixChoice(InstalledItem Item, VpsGame Game, VpsTableFile File, string Label);
