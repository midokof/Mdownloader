using System.Windows.Data;
using System.Windows.Markup;
using NovaDownload.App.Services;

namespace NovaDownload.App.Markup;

/// <summary>
/// <c>{loc:Tr Key}</c> — returns a live binding to the string catalog instead of a frozen
/// literal, so a language switch updates the text in place.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationProxy.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key
        };

        return binding.ProvideValue(serviceProvider);
    }
}
