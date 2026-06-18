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

      foreach (var ghDoc in FollowSelectionDocuments(rhinoDoc))
        IncludeFollowSelectionClipping(e, ghDoc);
    }

    /// <summary>Same geometry should participate in Zoom Extents.</summary>
    protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e)
    {
      CalculateBoundingBox(e);
    }

    private static void IncludeFollowSelectionClipping(CalculateBoundingBoxEventArgs e, GH_Document ghDoc)
    {
      if (!PreviewAllowed(ghDoc))
        return;

      foreach (var pObj in SelectedHiddenPreviewObjects(ghDoc))
      {
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

      foreach (var ghDoc in FollowSelectionDocuments(rhinoDoc))
      {
        if (!PreviewAllowed(ghDoc))
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

      foreach (var ghDoc in FollowSelectionDocuments(rhinoDoc))
      {
        if (!PreviewAllowed(ghDoc))
          continue;

        DrawWiresForDocument(ghDoc, e);
      }
    }

    /// <summary>Per-document preview gating (independent of which document we're drawing).</summary>
    private static bool PreviewAllowed(GH_Document ghDoc)
    {
      if (ghDoc.PreviewFilter != GH_PreviewFilter.None)
        return false;
      if (ghDoc.PreviewMode == GH_PreviewMode.Disabled)
        return false;
      return true;
    }

    /// <summary>
    /// Documents whose selected/hidden objects should be drawn for <paramref name="rhinoDoc"/>.
    /// <para>
    /// Editing a cluster loads a separate subsidiary document onto the canvas (see McNeel's cluster
    /// docs), and <see cref="GH_Canvas.Document"/> points at it while editing. A subsidiary document is
    /// identified by a non-null <see cref="GH_Document.Owner"/> (the owning cluster). The cluster object
    /// stays selected + hidden in the parent document the whole time, so if we drew the parent we'd keep
    /// previewing the whole cluster with the parent's settings. Instead, while a cluster document is open
    /// we draw ONLY that document — using its own selection state and preview colours — and skip the
    /// parent entirely. (Its <see cref="GH_Document.RhinoDocument"/> is non-null, so it cannot be
    /// distinguished by that; the ownership link is the reliable signal.)
    /// </para>
    /// Otherwise we fall back to the normal case: every server document bound to this Rhino file.
    /// </summary>
    private static IEnumerable<GH_Document> FollowSelectionDocuments(Rhino.RhinoDoc rhinoDoc)
    {
      var active = Instances.ActiveCanvas?.Document;
      if (active != null && active.Owner != null)
      {
        yield return active;
        yield break;
      }

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
      foreach (var pObj in SelectedHiddenPreviewObjects(ghDoc))
      {
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
      foreach (var pObj in SelectedHiddenPreviewObjects(ghDoc))
      {
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

    /// <summary>
    /// Selected + hidden preview objects in <paramref name="ghDoc"/>. When a cluster is being edited
    /// this document is the cluster's subsidiary edit document, so a flat scan correctly picks up the
    /// component you selected inside the cluster (a selected cluster object on a parent canvas is itself
    /// a hidden preview object and is handled the same way at that level).
    /// </summary>
    private static IEnumerable<IGH_PreviewObject> SelectedHiddenPreviewObjects(GH_Document ghDoc)
    {
      foreach (var obj in ghDoc.Objects)
      {
        if (!obj.Attributes.Selected)
          continue;
        if (obj is not IGH_PreviewObject pObj || !pObj.IsPreviewCapable || !pObj.Hidden)
          continue;

        yield return pObj;
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
