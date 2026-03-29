using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace SelectionPreview
{
  internal static class SelectionPreviewIcons
  {
    private const int IconSize = 24;

    /// <summary>Grasshopper library / assembly icon (cached; not handed to ToolStrip).</summary>
    internal static Bitmap AssemblyIcon => _assembly ??= NewFromBase();

    private static Bitmap? _assembly;
    private static Bitmap? _base;

    /// <summary>Decoded embedded toolbar art at 24×24 (cached).</summary>
    private static Bitmap Base
    {
      get
      {
        if (_base != null) return _base;
        _base = LoadEmbedded();
        return _base;
      }
    }

    /// <summary>New bitmap for <see cref="ToolStripButton.Image"/>; caller may dispose when replacing.</summary>
    internal static Bitmap MakeIcon(bool active)
    {
      var bmp = active ? NewFromBase() : DimmedCopy(Base);
      if (bmp.PixelFormat == PixelFormat.Format32bppArgb)
        return bmp;

      try
      {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        return bmp.Clone(rect, PixelFormat.Format32bppArgb);
      }
      finally
      {
        bmp.Dispose();
      }
    }

    private static Bitmap NewFromBase() => new Bitmap(Base);

    private static Bitmap LoadEmbedded()
    {
      var asm = Assembly.GetExecutingAssembly();
      using Stream? stream = asm.GetManifestResourceStream("SelectionPreview.Resources.toolbar.png");
      if (stream is null)
        return new Bitmap(IconSize, IconSize);

      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      ms.Position = 0;
      using var raw = new Bitmap(ms);

      if (raw.Width == IconSize && raw.Height == IconSize)
        return new Bitmap(raw);

      var scaled = new Bitmap(IconSize, IconSize);
      using (var g = Graphics.FromImage(scaled))
      {
        g.InterpolationMode     = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode       = PixelOffsetMode.Half;
        g.SmoothingMode         = SmoothingMode.None;
        g.CompositingQuality    = CompositingQuality.HighSpeed;
        g.Clear(Color.Transparent);
        g.DrawImage(raw, 0, 0, IconSize, IconSize);
      }

      return scaled;
    }

    private static Bitmap DimmedCopy(Bitmap src)
    {
      var bmp = new Bitmap(src.Width, src.Height);
      using (var g = Graphics.FromImage(bmp))
      {
        var m = new ColorMatrix(new[]
        {
          new[] { 0.4f, 0f,   0f,   0f, 0f },
          new[] { 0f,   0.4f, 0f,   0f, 0f },
          new[] { 0f,   0f,   0.4f, 0f, 0f },
          new[] { 0f,   0f,   0f,   1f, 0f },
          new[] { 0f,   0f,   0f,   0f, 1f },
        });
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(m);
        g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
      }

      return bmp;
    }
  }
}
