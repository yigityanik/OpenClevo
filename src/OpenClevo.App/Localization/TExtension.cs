using System.Windows.Markup;

namespace OpenClevo.Localization;

/// <summary>XAML markup: <c>Text="{loc:T common.apply}"</c> resolves the key in the current language.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(key);
}
