using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PulsarBattery.Tools;

namespace PulsarBattery.Controls;

public sealed partial class TranslatedTextBlock : UserControl
{
    public static readonly DependencyProperty TextStyleProperty =
        DependencyProperty.Register(nameof(TextStyle), typeof(Style), typeof(TranslatedTextBlock), new PropertyMetadata(null));

    public static readonly DependencyProperty TextWrappingProperty =
        DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(TranslatedTextBlock), new PropertyMetadata(TextWrapping.NoWrap));

    public static readonly DependencyProperty TextTrimmingProperty =
        DependencyProperty.Register(nameof(TextTrimming), typeof(TextTrimming), typeof(TranslatedTextBlock), new PropertyMetadata(TextTrimming.None));

    public static readonly DependencyProperty TextAlignmentProperty =
        DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(TranslatedTextBlock), new PropertyMetadata(TextAlignment.Left));

    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public string Text
    {
        set => ApplyText(value);
    }

    public TranslatedTextBlock()
    {
        this.InitializeComponent();
    }

    private void ApplyText(string text)
    {
        string translated = Loc.T(text);
        _textBlock.Text = translated;
        AutomationProperties.SetName(this, translated);
    }
}
