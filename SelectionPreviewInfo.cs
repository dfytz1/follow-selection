using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace SelectionPreview
{
  public class SelectionPreviewInfo : GH_AssemblyInfo
  {
    public override string Name => "Follow selection";

    public override Bitmap Icon => SelectionPreviewIcons.AssemblyIcon;

    public override string Description =>
      "Grasshopper canvas toolbar toggle: while enabled, selected hidden objects still draw in the Rhino viewport without changing Preview On/Off (safe for copy/paste).";

    public override Guid Id => new Guid("B4C3D2E1-F0A9-8B7C-6D5E-4F3A2B1C0D9E");

    public override string AuthorName => "GIA";

    public override string AuthorContact => "https://github.com/dfytz1/follow-selection";

    public override string AssemblyVersion => GetType().Assembly.GetName().Version?.ToString() ?? "1.0";
  }
}
