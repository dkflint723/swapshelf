using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DLSS_Swapper.Helpers;
using Windows.ApplicationModel.DataTransfer;

namespace DLSS_Swapper;

public partial class DiagnosticsWindowModel : ObservableObject
{
    /// <summary>The bundle as shown and as copied. Rebuilt when the checkbox changes.</summary>
    [ObservableProperty]
    public partial string DiagnosticsLog { get; set; } = string.Empty;

    /// <summary>
    /// Off by default. On, the paths that say where the user's games and profile live are left in.
    /// The checkbox says so in as many words; the default is the one that is safe to paste anywhere.
    /// </summary>
    [ObservableProperty]
    public partial bool IncludeRealPaths { get; set; } = false;

    public DiagnosticsWindowModelTranslationProperties TranslationProperties { get; } = new DiagnosticsWindowModelTranslationProperties();

    public DiagnosticsWindowModel() : base()
    {
        Rebuild();
    }

    partial void OnIncludeRealPathsChanged(bool value)
    {
        Rebuild();
    }

    void Rebuild()
    {
        DiagnosticsLog = DiagnosticsBundle.Build(redact: IncludeRealPaths == false);
    }

    [RelayCommand]
    void CopyText()
    {
        var package = new DataPackage();
        package.SetText(DiagnosticsLog);
        Clipboard.SetContent(package);
    }
}
