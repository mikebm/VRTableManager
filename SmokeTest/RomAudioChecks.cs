using VrTableManager.Services;

/// <summary>
/// The pure parts of recording a table's ROM sound (RomAudio): finding the trough switches in the
/// script shapes real tables use, and cutting the recording down. Running PinMAME itself is checked
/// by the lobby (it needs VPX's pinmame64.dll and a ROM).
/// </summary>
public static class RomAudioChecks
{
    public static IEnumerable<(bool ok, string name)> Run()
    {
        // Stern SAM (Avatar, X-Men): vpmBallStack; the first number is the entry switch.
        yield return (RomAudio.TroughSwitchesIn("Set bsTrough = New cvpmBallStack\n bsTrough.InitSw 0, 21, 20, 19, 18, 0, 0, 0\n")
                .SequenceEqual([21, 20, 19, 18]), "trough switches from bsTrough.InitSw (entry switch left out)");
        // VPW physical trough (Medieval Madness, Fish Tales).
        yield return (RomAudio.TroughSwitchesIn("Sub sw34_Hit():Controller.Switch(34) = 1:UpdateTrough:End Sub\nSub sw34_UnHit():Controller.Switch(34) = 0:UpdateTrough:End Sub\nSub sw33_Hit():Controller.Switch(33) = 1:UpdateTrough:End Sub\n")
                .SequenceEqual([34, 33]), "trough switches from sw_Hit subs that update the trough");
        // cvpmTrough in a With block (Attack from Mars).
        yield return (RomAudio.TroughSwitchesIn("Set bsTrough = New cvpmTrough\nWith bsTrough\n    .size = 4\n    .initSwitches Array(32, 33, 34, 35)\nEnd With\nWith bsL\n .initSwitches Array(36)\nEnd With\n")
                .SequenceEqual([32, 33, 34, 35]), "trough switches from a cvpmTrough's initSwitches (other ball stacks left out)");
        yield return (RomAudio.TroughSwitchesIn("' bsTrough.InitSw 0, 1, 2\nSub Target5_Hit: Controller.Switch(5) = 1: End Sub\n").Count == 0,
            "commented-out lines and non-trough switches are ignored");

        // Clip: silence before Start and after the music is cut, the rest normalised and faded out.
        const int rate = 1000;
        var samples = new short[60 * rate];
        for (var i = 30 * rate; i < 40 * rate; i++) samples[i] = (short)(i % 2 == 0 ? 1000 : -1000); // 10 s of quiet sound
        var clip = RomAudio.Clip(samples, 1, rate);
        yield return (clip != null && clip.Length is > 10 * rate and < 11 * rate,
            $"clip keeps the sound and drops the silence around it ({clip?.Length / (double)rate:F1} s)");
        yield return (clip != null && clip.Max() > 20000 && Math.Abs((int)clip[^1]) < 100,
            "clip is normalised and fades out");
        yield return (RomAudio.Clip(new short[60 * rate], 1, rate) == null, "a silent recording gives nothing");
    }
}
