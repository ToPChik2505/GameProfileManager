using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GameProfileManager;

public sealed class GameIconView : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(ImageSource), typeof(GameIconView),
        new PropertyMetadata(null, OnIconChanged));

    private readonly FontIcon _placeholder = new() { Glyph = "\uE7F1" };
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };

    public ImageSource? Icon
    {
        get => (ImageSource?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public GameIconView()
    {
        var content = new Grid();
        content.Children.Add(_placeholder);
        content.Children.Add(_image);
        Content = content;
        _image.ImageOpened += (_, _) => _placeholder.Visibility = Visibility.Collapsed;
        _image.ImageFailed += (_, _) => _placeholder.Visibility = Visibility.Visible;
    }

    private static void OnIconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (GameIconView)sender;
        view._placeholder.Visibility = args.NewValue is ImageSource ? Visibility.Collapsed : Visibility.Visible;
        view._image.Source = args.NewValue as ImageSource;
    }
}
