namespace Kiosk.Client;

/// <summary>
/// Icons of the shell: Windows' own icon font (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10)
/// for Kiosk functions, and the program's own icon for applications.
/// </summary>
internal static class KioskIcons
{
    internal const string Menu = "", Browser = "", Remote = "", Notes = "", Settings = "",
        Lock = "", Logout = "", Desktop = "", Command = "", Refresh = "", App = "", Screens = "";

    private static string ResolveFamily()
    {
        using var collection = new System.Drawing.Text.InstalledFontCollection();
        return collection.Families.Select(f => f.Name).FirstOrDefault(n => n is "Segoe Fluent Icons" or "Segoe MDL2 Assets") ?? "Segoe UI Symbol";
    }
    private static readonly string Family = ResolveFamily();
    private static readonly Dictionary<string, Image?> programIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's icon (cached); null when the file has none or is missing.</summary>
    internal static Image? ForProgram(string path)
    {
        if (programIcons.TryGetValue(path, out var image)) return image;
        try
        {
            if (File.Exists(path)) { using var icon = Icon.ExtractAssociatedIcon(path); image = icon?.ToBitmap(); }
        }
        catch (Exception) { image = null; }
        return programIcons[path] = image;
    }

    /// <summary>Draws an image icon, or else a glyph of the icon font, centred in <paramref name="bounds"/>.</summary>
    internal static void Draw(Graphics g, Rectangle bounds, string? glyph, Image? image, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (image != null)
        {
            int size = Math.Min(bounds.Width, bounds.Height);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, new Rectangle(bounds.Left + (bounds.Width - size) / 2, bounds.Top + (bounds.Height - size) / 2, size, size));
            return;
        }
        if (string.IsNullOrEmpty(glyph)) return;
        using var font = new Font(Family, Math.Max(6, Math.Min(bounds.Width, bounds.Height) * 0.72f), GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, glyph, font, bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}
