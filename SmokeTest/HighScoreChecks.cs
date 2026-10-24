using VrTableManager.Services;

/// <summary>
/// High-score decoding (HighScores) on synthetic .nv data shaped like real Pinball Memory Maps
/// entries (WPC, Stern SAM, System 7, System 11 char maps). No network, no cached maps needed.
/// </summary>
public static class HighScoreChecks
{
    const string Wpc = """{ "endian": "big", "memory_layout": [ { "address": "0x0000", "size": "0x2000", "type": "nvram" }, { "address": "0x8000", "size": "0x8000", "type": "rom" } ] }""";
    const string Sam = """{ "endian": "little", "memory_layout": [ { "address": "0x00000000", "size": "0x100000", "type": "ram" }, { "address": "0x02100000", "size": "0x20000", "type": "nvram" } ] }""";
    const string Sys7 = """{ "endian": "big", "memory_layout": [ { "address": "0x0000", "size": "0x100", "type": "ram" }, { "address": "0x0100", "size": "0x100", "type": "nvram", "nibble": "low" } ] }""";

    public static IEnumerable<(bool ok, string name)> Run()
    {
        // WPC (Attack from Mars layout): "ch" initials + 6-byte BCD score; unused entry ("   ", 0) left out.
        var nv = new byte[0x2000 + 6];
        Put(nv, 7473, "ABC"u8);
        Put(nv, 7476, [0x00, 0x12, 0x34, 0x56, 0x78, 0x90]);
        Put(nv, 7435, "   "u8);
        Put(nv, 7444, "DEF"u8);
        Put(nv, 7447, [0x00, 0x09, 0x87, 0x6A, 0x5F, 0x40]);   // 0xA–0xF nibbles count as 0
        const string afm = """
        { "_fileformat": 0.9, "_metadata": { "platform": "williams-wpc-12K" },
          "high_scores": [
            { "label": "Grand Champion", "initials": { "start": 7473, "encoding": "ch", "length": 3 }, "score": { "start": 7476, "encoding": "bcd", "length": 6 } },
            { "label": "First Place", "initials": { "start": 7435, "encoding": "ch", "length": 3 }, "score": { "start": 7438, "encoding": "bcd", "length": 6 } },
            { "label": "Second Place", "initials": { "start": "0x1D14", "encoding": "ch", "length": 3 }, "score": { "start": "0x1D17", "encoding": "bcd", "length": 6 } } ],
          "mode_champions": [
            { "label": "Ruler of the Universe", "initials": { "start": 7473, "encoding": "ch", "length": 3 }, "timestamp": { "start": 7412, "encoding": "wpc_rtc", "length": 7 } } ] }
        """;
        var s = HighScores.Decode(afm, Wpc, nv);
        yield return (s.Count == 3 && s[0] == new HighScore("Grand Champion", "ABC", 1234567890), "high scores: WPC initials + BCD score (Grand Champion ABC 1,234,567,890)");
        yield return (s.Count == 3 && s[1] == new HighScore("Second Place", "DEF", 987605040), "high scores: unused entry skipped, hex addresses, A–F BCD nibbles read as 0");
        yield return (s.Count == 3 && s[2] is { Label: "Ruler of the Universe", Initials: "ABC", Score: 0 }, "high scores: mode champion with initials only");

        // Stern SAM: NVRAM at 0x02100000, little-endian 4-byte int, null-terminated 11-char initials.
        nv = new byte[0x20000 + 6];
        Array.Fill(nv, (byte)0xFF);
        Put(nv, 0x3000, "G S\0"u8);
        Put(nv, 0x3018, [0xC0, 0x68, 0x78, 0x04]);
        Put(nv, 0x3020, "JOHN SMITH!"u8);
        Put(nv, 0x3038, [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]);
        const string xmn = """
        { "_metadata": { "platform": "stern-sam" },
          "high_scores": [
            { "label": "Grand Champion", "initials": { "start": "0x02103000", "encoding": "ch", "null": "terminate", "length": 11 }, "score": { "start": "0x02103018", "encoding": "int", "length": 4 } },
            { "label": "First Place", "initials": { "start": "0x02103020", "encoding": "ch", "null": "terminate", "length": 11 }, "score": { "start": "0x02103038", "encoding": "int", "length": 8 } } ] }
        """;
        s = HighScores.Decode(xmn, Sam, nv);
        yield return (s.Count == 2 && s[0] == new HighScore("Grand Champion", "G S", 75_000_000), "high scores: Stern SAM little-endian int, null-terminated initials");
        yield return (s.Count == 2 && s[1] == new HighScore("First Place", "JOHN SMITH!", 4_294_967_296), "high scores: 8-byte int beyond 32 bits, initials filling the field");

        // Williams System 7: 4-bit NVRAM at 0x100 (low nibbles only), and a score with "scale".
        nv = new byte[0x100 + 6];
        Put(nv, 0x66, [0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7]);
        Put(nv, 0x80, [0x31, 0x22]);
        const string sys7 = """
        { "_metadata": { "platform": "williams-system7-10K" },
          "high_scores": [
            { "label": "First Place", "score": { "start": "0x0166", "encoding": "bcd", "length": 8 } },
            { "label": "Scaled", "score": { "start": "0x0180", "encoding": "bcd", "length": 2, "scale": 1000 } } ] }
        """;
        s = HighScores.Decode(sys7, Sys7, nv);
        yield return (s.Count == 2 && s[0] == new HighScore("First Place", "", 1234567), "high scores: low-nibble NVRAM (System 7) combines nibbles");
        yield return (s.Count == 2 && s[1].Score == 12_000, "high scores: scale applied");

        // System 11 char_map, odd-length nibble score (Data East Robocop style), endian override, repeat.
        nv = new byte[0x2000];
        Put(nv, 0x100, [11, 12, 0]);                          // "AB " through the char map
        Put(nv, 0x110, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        Put(nv, 0x120, [0x34, 0x12]);
        for (var i = 0; i < 3; i++) { Put(nv, 0x200 + i * 5, "XYZ"u8); nv[0x200 + i * 5 + 4] = (byte)(i + 1); }
        Put(nv, 0x205, [0xFF, 0xFF, 0xFF, 0x00, 0x00]);           // #2 unused
        const string s11 = """
        { "_metadata": { "platform": "williams-system11-48K", "char_map": " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ" },
          "high_scores": [
            { "label": "Champion", "initials": { "start": 256, "encoding": "ch", "length": 3 }, "score": { "start": 272, "encoding": "bcd", "length": 7, "nibble": "low" } },
            { "label": "Little", "score": { "start": 288, "encoding": "int", "length": 2, "endian": "little" } } ] }
        """;
        s = HighScores.Decode(s11, Wpc, nv);
        yield return (s.Count == 2 && s[0] == new HighScore("Champion", "AB", 1234567), "high scores: char_map initials, odd-length low-nibble BCD");
        yield return (s.Count == 2 && s[1].Score == 0x1234, "high scores: per-entry endian override");
        const string repeat = """
        { "_metadata": { "platform": "williams-wpc-8K" },
          "mode_champions": [
            { "label": "Champ #{#}", "initials": { "start": 512, "encoding": "ch", "length": 3, "default": "ÿÿÿ" }, "score": { "start": 516, "encoding": "int", "length": 1 }, "repeat": { "count": 3, "step": 5 } } ] }
        """;
        var champs = HighScores.Decode(repeat, Wpc, nv);
        yield return (champs.Count == 2 && champs[0] == new HighScore("Champ #1", "XYZ", 1) && champs[1] == new HighScore("Champ #3", "XYZ", 3),
            "high scores: repeat numbers labels and steps addresses; \"ÿÿÿ\" default initials mean unused");

        // The lobby's two DMD lines: the champion with its label, then the rest by rank; initials-only entries skipped.
        var lines = VrTableManager.Lobby.LobbyText.HighScoreLines(
            [new("Grand Champion", "ABC", 1234567890), new("Ruler of the Universe", "XYZ", 0), new("First Place", "DEF", 900_000_000), new("Second Place", "", 800_000_000)]);
        yield return (lines.SequenceEqual(["GRAND CHAMPION  ABC  1,234,567,890", "1 DEF 900,000,000  ·  2 800,000,000"]), "high scores: lobby DMD lines");
        yield return (VrTableManager.Lobby.LobbyText.HighScoreLines([new("High Score #1", "RJW", 6_000_000), new("High Score #2", "DAV", 5_000_000)])[1] == "2 DAV 5,000,000",
            "high scores: without a champion, ranks continue from 2");
        yield return (VrTableManager.Lobby.LobbyText.HighScoreLines([]).Count == 0, "high scores: nothing to show, no lines");

        // Bad input never throws.
        yield return (HighScores.Decode(afm, Wpc, new byte[100]).Count == 0, "high scores: short .nv gives nothing");
        yield return (HighScores.Decode("not json", null, nv).Count == 0, "high scores: bad map gives nothing");
        yield return (HighScores.Read("afm_113b", Path.Combine(Path.GetTempPath(), "no-such-dir", "afm_113b.nv")).Count == 0, "high scores: missing .nv gives nothing");
    }

    static void Put(byte[] nv, int at, ReadOnlySpan<byte> bytes) => bytes.CopyTo(nv.AsSpan(at));
}
