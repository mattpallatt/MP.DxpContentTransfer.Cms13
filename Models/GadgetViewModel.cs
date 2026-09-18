namespace DxpContentTransfer.Cms13.Models;

public class GadgetViewModel
{
    public string ContentId { get; set; }
    public string ContentName { get; set; }
    public string CurrentEnvironmentName { get; set; }
    public List<DxpEnvironmentConfig> AvailableTargets { get; set; } = new();
    public bool IsSettingsConfigured { get; set; }
    public bool IsPageContent { get; set; } = true;
    public bool IsPublished { get; set; } = true;

    // The languages the selected content exists in, for the form's language picker. Populated
    // server-side (from ILocalizable.ExistingLanguages) so the picker renders on the initial panel.
    public List<LanguageOption> AvailableLanguages { get; set; } = new();
}
