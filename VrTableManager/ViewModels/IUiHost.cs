namespace VrTableManager.ViewModels;

/// <summary>What the view models need from the window (pickers, dialogs, minimize/restore).</summary>
public interface IUiHost
{
    Task<string?> PickFolderAsync(string title);

    /// <param name="preferredName">A file name to filter to first (e.g. "afm_113b.zip"); <paramref name="pattern"/> stays as the other choice.</param>
    /// <param name="startFolder">Folder the picker opens in, when it exists.</param>
    Task<string?> PickFileAsync(
        string title,
        string pattern,
        string? preferredName = null,
        string? startFolder = null
    );

    /// <param name="okLabel">The button that says yes (most confirmations are for deleting something).</param>
    Task<bool> ConfirmAsync(string title, string message, string okLabel = "Delete");

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    Task<string?> AskTextAsync(string title, string message, string initial, string okLabel);

    /// <summary>Shows a message with several buttons; returns the index pressed, or -1 if the window was closed.</summary>
    /// <param name="details">Optional monospaced text (e.g. log lines) shown in a scrolling box under the message.</param>
    Task<int> ChoiceAsync(string title, string message, string[] buttons, string? details = null);
    Task<string?> PickSaveFileAsync(
        string title,
        string suggestedName,
        string pattern,
        string? startFolder = null
    );
    void HideForPlay();
    void ShowAfterPlay();
    void OpenExternal(string pathOrUrl);
}
