using Microsoft.UI.Xaml.Markup;

namespace NaxExplorer;

/// <summary>A text in the interface language, for XAML: {local:T Key=Open}.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class T : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Key);
}
