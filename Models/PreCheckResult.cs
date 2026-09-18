namespace DxpContentTransfer.Cms13.Models;

public class PreCheckResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public List<PreCheckItemResult> Items { get; set; } = new();

    // The distinct languages the selected content exists in, for the gadget's language picker.
    public List<LanguageOption> AvailableLanguages { get; set; } = new();

    public int OverwriteCount => Items.Count(i => i.Action == PreCheckAction.Overwrite);
    public int CreateNewCount => Items.Count(i => i.Action == PreCheckAction.CreateNew);
    public int CreateCount => Items.Count(i => i.Action == PreCheckAction.Create);
    public int UnresolvableCount => Items.Count(i => i.Action == PreCheckAction.Unresolvable);
}

// A language branch the content exists in: Code is the locale ("en-GB"), DisplayName the friendly
// name ("English (United Kingdom)"). The picker shows "{DisplayName} [{Code}]".
public class LanguageOption
{
    public string Code { get; set; }
    public string DisplayName { get; set; }

    // The master language is a structural prerequisite — it always transfers (it owns the
    // culture-invariant properties), so the picker pins it checked-and-disabled.
    public bool IsMaster { get; set; }
}
