namespace WFly.UI.Controls;

/// <summary>
/// The application mark used consistently by the window, sidebar and tray.
/// It is embedded in the executable so the portable release needs no external
/// image file at runtime.
/// </summary>
internal static class AppIconFactory
{
    private const string ImageResourceName = "WFly.Assets.wfly-logo-blue.png";
    private const string IconResourceName = "WFly.Assets.wfly.ico";
    private static Icon? _icon;
    private static Bitmap? _image;

    public static Icon Instance => _icon ??= CreateIcon();
    public static Image Image => _image ??= CreateImage();

    private static Icon CreateIcon()
    {
        using var stream = typeof(AppIconFactory).Assembly.GetManifestResourceStream(IconResourceName)
            ?? throw new InvalidOperationException("找不到已嵌入的 WFly 图标资源。");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    private static Bitmap CreateImage()
    {
        using var stream = typeof(AppIconFactory).Assembly.GetManifestResourceStream(ImageResourceName)
            ?? throw new InvalidOperationException("找不到已嵌入的 WFly Logo 资源。");
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }
}
