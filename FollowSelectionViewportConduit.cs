using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Display;
using Rhino.Geometry;

namespace SelectionPreview
{
  /// <summary>
  /// Draws viewport preview for selected objects that are still <see cref="IGH_PreviewObject.Hidden"/> when
  /// the document uses <see cref="GH_PreviewFilter.None"/> (Grasshopper skips hidden objects entirely).
  /// Shaded faces/meshes use <see cref="PostDrawObjects"/> (depth-tested); wires/points/edges use
  /// <see cref="DrawForeground"/> so they read on top like standard GH preview accents.
  /// Does not modify <c>Hidden</c>, so copy/paste and Preview On/Off keep working.
  /// </summary>
  internal sealed class FollowSelectionViewportConduit : DisplayConduit
  {
    internal static FollowSelectionViewportConduit Instance { get; } = new();

    private static readonly ConstructorInfo? PreviewArgsCtor = typeof(GH_PreviewArgs).GetConstructor(
      BindingFlags.Instance | BindingFlags.NonPublic,
      null,
      new[]
      {
        typeof(GH_Document), typeof(DisplayPipeline), typeof(RhinoViewport), typeof(int),
        typeof(Color), typeof(Color), typeof(DisplayMaterial), typeof(DisplayMaterial), typeof(MeshingParameters),
      },
      null);

    private FollowSelectionViewportConduit()
    {
      Enabled = false;
    }

    /// <summary>Feature flag read each frame; toggled from toolbar.</summary>
    internal static bool FeatureEnabled { get; set; }

    /// <summary>
    /// Expand the scene bounding box so near/far clipping includes our extra preview geometry
    /// (otherwise Rhino clips it when zooming in close).
    /// </summary>
    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
      if (!FeatureEnabled || PreviewArgsCtor == null)
        return;

      var rhinoDoc = e.Viewport?.ParentView?.Document;
      if (rhinoDoc == null || rhinoDoc.IsHeadless)
        return;

      foreach (var ghDoc in MatchingGrasshopperDocuments(rhinoDoc))
        IncludeFollowSelectionClipping(e, ghDoc);
    }

    /// <summary>Same geometry should participate in Zoom Extents.</summary>
    protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e)
    {
      CalculateBoundingBox(e);
    }

    private static void IncludeFollowSelectionClipping(CalculateBoundingBoxEventArgs e, GH_Document ghDoc)
    {
      if (ghDoc.PreviewFilter != GH_PreviewFilter.None)
        return;
      if (ghDoc.PreviewMode == GH_PreviewMode.Disabled)
        return;

      foreach (var obj in ghDoc.Objects)
      {
        if (!obj.Attributes.Selected)
          continue;
        if (obj is not IGH_PreviewObject pObj || !pObj.IsPreviewCapable || !pObj.Hidden)
          continue;

        var cb = pObj.ClippingBox;
        if (cb.IsValid)
          e.IncludeBoundingBox(cb);
      }
    }

    /// <summary>
    /// Shaded surfaces / mesh faces only — depth testing on so Rhino geometry occludes them.
    /// </summary>
    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (!FeatureEnabled || PreviewArgsCtor == null)
        return;

      var rhinoDoc = e.Viewport.ParentView?.Document;
      if (rhinoDoc == null || rhinoDoc.IsHeadless)
        return;

      for (var i = 0; i < Instances.DocumentServer.DocumentCount; i++)
      {
        var ghDoc = Instances.DocumentServer[i];
        if (!ShouldDrawFollowSelection(ghDoc, rhinoDoc))
          continue;
        if (ghDoc.PreviewMode != GH_PreviewMode.Shaded)
          continue;

        DrawMeshesForDocument(ghDoc, e);
      }
    }

    /// <summary>
    /// Wires, points, and brep/mesh edges — depth testing off so they stay readable on top of the scene.
    /// </summary>
    protected override void DrawForeground(DrawEventArgs e)
    {
      if (!FeatureEnabled || PreviewArgsCtor == null)
        return;

      var rhinoDoc = e.Viewport.ParentView?.Document;
      if (rhinoDoc == null || rhinoDoc.IsHeadless)
        return;

      for (var i = 0; i < Instances.DocumentServer.DocumentCount; i++)
      {
        var ghDoc = Instances.DocumentServer[i];
        if (!ShouldDrawFollowSelection(ghDoc, rhinoDoc))
          continue;

        DrawWiresForDocument(ghDoc, e);
      }
    }

    private static bool ShouldDrawFollowSelection(GH_Document ghDoc, Rhino.RhinoDoc rhinoDoc)
    {
      if (ghDoc.RhinoDocument != rhinoDoc)
        return false;
      if (ghDoc.PreviewFilter != GH_PreviewFilter.None)
        return false;
      if (ghDoc.PreviewMode == GH_PreviewMode.Disabled)
        return false;
      return true;
    }

    /// <summary>Documents on the server that belong to this Rhino file.</summary>
    private static IEnumerable<GH_Document> MatchingGrasshopperDocuments(Rhino.RhinoDoc rhinoDoc)
    {
      var server = Instances.DocumentServer;
      for (var i = 0; i < server.DocumentCount; i++)
      {
        var ghDoc = server[i];
        if (ghDoc.RhinoDocument == rhinoDoc)
          yield return ghDoc;
      }
    }

    private static void DrawMeshesForDocument(GH_Document ghDoc, DrawEventArgs e)
    {
      var args = CreatePreviewArgs(ghDoc, e, forSelectedWires: false);
      foreach (var obj in ghDoc.Objects)
      {
        if (!obj.Attributes.Selected)
          continue;
        if (obj is not IGH_PreviewObject pObj || !pObj.IsPreviewCapable || !pObj.Hidden)
          continue;

        try
        {
          pObj.DrawViewportMeshes(args);
        }
        catch
        {
          /* ignore single-object preview failures */
        }
      }
    }

    private static void DrawWiresForDocument(GH_Document ghDoc, DrawEventArgs e)
    {
      var argsSel = CreatePreviewArgs(ghDoc, e, forSelectedWires: true);
      foreach (var obj in ghDoc.Objects)
      {
        if (!obj.Attributes.Selected)
          continue;
        if (obj is not IGH_PreviewObject pObj || !pObj.IsPreviewCapable || !pObj.Hidden)
          continue;

        try
        {
          pObj.DrawViewportWires(argsSel);
        }
        catch
        {
          /* ignore single-object preview failures */
        }
      }
    }

    private static IGH_PreviewArgs CreatePreviewArgs(GH_Document ghDoc, DrawEventArgs e, bool forSelectedWires)
    {
      var meshParams = ghDoc.PreviewCurrentMeshParameters() ?? MeshingParameters.Default;
      var wire = Color.FromArgb(ghDoc.PreviewColour.R, ghDoc.PreviewColour.G, ghDoc.PreviewColour.B);
      var wireSel =
        Color.FromArgb(ghDoc.PreviewColourSelected.R, ghDoc.PreviewColourSelected.G, ghDoc.PreviewColourSelected.B);
      var face = GH_Material.CreateStandardMaterial(ghDoc.PreviewColour);
      var faceSel = GH_Material.CreateStandardMaterial(ghDoc.PreviewColourSelected);
      var defaultThickness = e.Display.DefaultCurveThickness;
      var thickThickness = defaultThickness + CentralSettings.PreviewSelectionThickening;
      var thickness = forSelectedWires ? thickThickness : defaultThickness;

      return (IGH_PreviewArgs)PreviewArgsCtor!.Invoke(new object[]
      {
        ghDoc, e.Display, e.Viewport, thickness, wire, wireSel, face, faceSel, meshParams,
      });
    }
  }
}
