using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace Hime.Commands;

/// <summary>Draws a small cached cyberpunk command card without browser dependencies.</summary>
[SupportedOSPlatform("windows6.1")]
internal static class HelpMenuRenderer
{
    private const int CanvasWidth = 1200;
    private const int OuterPadding = 56;
    private const int ColumnGap = 26;
    private const int CardPadding = 24;
    private const int HeaderHeight = 170;
    private const int FooterHeight = 74;
    private const int EntryHeight = 50;
    private static readonly object RenderSync = new();

    public static string Render(IReadOnlyList<HelpSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var cacheDirectory = Path.Combine(AppContext.BaseDirectory, "runtime", "help");
        Directory.CreateDirectory(cacheDirectory);
        var signature = string.Join('\n', sections.SelectMany(section =>
            section.Entries.Select(entry => $"{section.Name}|{entry.Command}|{entry.Description}")));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..10].ToLowerInvariant();
        var outputPath = Path.Combine(cacheDirectory, $"help-menu-{hash}.png");

        lock (RenderSync)
        {
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
                return outputPath;

            RenderCore(sections, outputPath);
            return outputPath;
        }
    }

    private static void RenderCore(IReadOnlyList<HelpSection> sections, string outputPath)
    {
        var columns = PackColumns(sections);
        var contentHeight = Math.Max(columns.LeftHeight, columns.RightHeight);
        var canvasHeight = HeaderHeight + contentHeight + FooterHeight + OuterPadding;
        using var bitmap = new Bitmap(CanvasWidth, Math.Max(640, canvasHeight), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var background = new LinearGradientBrush(
                   new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                   Color.FromArgb(9, 11, 25),
                   Color.FromArgb(29, 8, 38),
                   125f))
        {
            graphics.FillRectangle(background, 0, 0, bitmap.Width, bitmap.Height);
        }
        DrawGrid(graphics, bitmap.Size);
        DrawHeader(graphics, sections.Sum(section => section.Entries.Count));

        var columnWidth = (CanvasWidth - OuterPadding * 2 - ColumnGap) / 2;
        DrawColumn(graphics, columns.Left, OuterPadding, HeaderHeight, columnWidth);
        DrawColumn(graphics, columns.Right, OuterPadding + columnWidth + ColumnGap, HeaderHeight, columnWidth);
        DrawFooter(graphics, bitmap.Width, bitmap.Height);

        var temp = outputPath + ".tmp";
        bitmap.Save(temp, ImageFormat.Png);
        File.Move(temp, outputPath, overwrite: true);
    }

    private static (IReadOnlyList<HelpSection> Left, IReadOnlyList<HelpSection> Right, int LeftHeight, int RightHeight)
        PackColumns(IReadOnlyList<HelpSection> sections)
    {
        var left = new List<HelpSection>();
        var right = new List<HelpSection>();
        var leftHeight = 0;
        var rightHeight = 0;
        foreach (var section in sections.OrderByDescending(CardHeight))
        {
            var height = CardHeight(section);
            if (leftHeight <= rightHeight)
            {
                left.Add(section);
                leftHeight += height + ColumnGap;
            }
            else
            {
                right.Add(section);
                rightHeight += height + ColumnGap;
            }
        }
        return (left, right, Math.Max(0, leftHeight - ColumnGap), Math.Max(0, rightHeight - ColumnGap));
    }

    private static int CardHeight(HelpSection section) => 66 + section.Entries.Count * EntryHeight + CardPadding;

    private static void DrawHeader(Graphics graphics, int commandCount)
    {
        using var cyan = new SolidBrush(Color.FromArgb(81, 247, 255));
        using var pink = new SolidBrush(Color.FromArgb(255, 74, 192));
        using var white = new SolidBrush(Color.FromArgb(238, 244, 255));
        using var dim = new SolidBrush(Color.FromArgb(148, 164, 194));
        using var title = new Font("Microsoft YaHei UI", 31, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subtitle = new Font("Microsoft YaHei UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
        using var counter = new Font("Consolas", 18, FontStyle.Bold, GraphicsUnit.Pixel);

        graphics.FillRectangle(pink, OuterPadding, 40, 8, 82);
        graphics.DrawString("HIME // COMMAND MATRIX", title, white, OuterPadding + 28, 35);
        graphics.DrawString("秧秧智能终端 · 自适应指令索引", subtitle, dim, OuterPadding + 30, 92);
        graphics.DrawString($"ONLINE  ·  {commandCount:00} COMMANDS", counter, cyan, CanvasWidth - OuterPadding - 300, 55);
        using var line = new Pen(Color.FromArgb(95, 81, 247, 255), 2);
        graphics.DrawLine(line, OuterPadding, 140, CanvasWidth - OuterPadding, 140);
    }

    private static void DrawColumn(
        Graphics graphics,
        IReadOnlyList<HelpSection> sections,
        int x,
        int y,
        int width)
    {
        foreach (var section in sections)
        {
            var height = CardHeight(section);
            DrawCard(graphics, section, new Rectangle(x, y, width, height));
            y += height + ColumnGap;
        }
    }

    private static void DrawCard(Graphics graphics, HelpSection section, Rectangle bounds)
    {
        using var path = RoundedRectangle(bounds, 18);
        using var fill = new SolidBrush(Color.FromArgb(205, 14, 20, 39));
        using var glow = new Pen(Color.FromArgb(150, 76, 225, 255), 2);
        graphics.FillPath(fill, path);
        graphics.DrawPath(glow, path);

        using var accent = new SolidBrush(Color.FromArgb(255, 75, 195));
        using var headingBrush = new SolidBrush(Color.FromArgb(242, 247, 255));
        using var commandBrush = new SolidBrush(Color.FromArgb(92, 239, 255));
        using var descriptionBrush = new SolidBrush(Color.FromArgb(178, 190, 215));
        using var headingFont = new Font("Microsoft YaHei UI", 21, FontStyle.Bold, GraphicsUnit.Pixel);
        using var commandFont = new Font("Consolas", 17, FontStyle.Bold, GraphicsUnit.Pixel);
        using var descriptionFont = new Font("Microsoft YaHei UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);

        graphics.FillRectangle(accent, bounds.X + CardPadding, bounds.Y + 22, 7, 27);
        graphics.DrawString(section.Name.ToUpperInvariant(), headingFont, headingBrush, bounds.X + CardPadding + 20, bounds.Y + 18);
        var rowY = bounds.Y + 66;
        foreach (var entry in section.Entries)
        {
            graphics.DrawString(Trim(entry.Command, 24), commandFont, commandBrush, bounds.X + CardPadding, rowY + 4);
            graphics.DrawString(Trim(entry.Description, 24), descriptionFont, descriptionBrush, bounds.X + 228, rowY + 5);
            using var separator = new Pen(Color.FromArgb(30, 150, 191, 220));
            graphics.DrawLine(separator, bounds.X + CardPadding, rowY + EntryHeight - 5, bounds.Right - CardPadding, rowY + EntryHeight - 5);
            rowY += EntryHeight;
        }
    }

    private static void DrawGrid(Graphics graphics, Size size)
    {
        using var grid = new Pen(Color.FromArgb(18, 83, 225, 255), 1);
        for (var x = 0; x < size.Width; x += 40)
            graphics.DrawLine(grid, x, 0, x, size.Height);
        for (var y = 0; y < size.Height; y += 40)
            graphics.DrawLine(grid, 0, y, size.Width, y);
        using var scanline = new Pen(Color.FromArgb(10, 255, 255, 255), 1);
        for (var y = 2; y < size.Height; y += 6)
            graphics.DrawLine(scanline, 0, y, size.Width, y);
    }

    private static void DrawFooter(Graphics graphics, int width, int height)
    {
        using var brush = new SolidBrush(Color.FromArgb(125, 145, 175));
        using var accent = new SolidBrush(Color.FromArgb(255, 77, 193));
        using var font = new Font("Microsoft YaHei UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString("输入 /帮助 刷新菜单  ·  HIME LOCAL RENDER", font, brush, OuterPadding, height - 50);
        graphics.FillRectangle(accent, width - OuterPadding - 86, height - 43, 86, 4);
    }

    private static string Trim(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[..(maxCharacters - 1)] + "…";

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
