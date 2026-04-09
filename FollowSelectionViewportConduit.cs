using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    /// <summary>Per <see cref="GH_Document"/> cache: preview colours → face materials; mesh type/custom → meshing params.</summary>
    private sealed class FollowPreviewCache
    {
      internal int PreviewArgb;
      internal int PreviewSelArgb;
      internal DisplayMaterial? Face;
      internal DisplayMaterial? FaceSel;

      internal GH_PreviewMesh MeshType;
      internal MeshingParameters? CustomMeshSnap;
      internal MeshingParameters? MeshCopy;
    }

    private static readonly ConditionalWeakTable<GH_Document, FollowPreviewCache> PreviewCaches = new();

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
      var cache = PreviewCaches.GetValue(ghDoc, _ => new FollowPreviewCache());
      EnsureFaceMaterials(ghDoc, cache);
      var meshParams = GetCachedMeshingParameters(ghDoc, cache);

      var wire = Color.FromArgb(ghDoc.PreviewColour.R, ghDoc.PreviewColour.G, ghDoc.PreviewColour.B);
      var wireSel =
        Color.FromArgb(ghDoc.PreviewColourSelected.R, ghDoc.PreviewColourSelected.G, ghDoc.PreviewColourSelected.B);
      var defaultThickness = e.Display.DefaultCurveThickness;
      var thickThickness = defaultThickness + CentralSettings.PreviewSelectionThickening;
      var thickness = forSelectedWires ? thickThickness : defaultThickness;

      return (IGH_PreviewArgs)PreviewArgsCtor!.Invoke(new object[]
      {
        ghDoc, e.Display, e.Viewport, thickness, wire, wireSel, cache.Face!, cache.FaceSel!, meshParams,
      });
    }

    private static void EnsureFaceMaterials(GH_Document ghDoc, FollowPreviewCache cache)
    {
      var p = Color.FromArgb(ghDoc.PreviewColour.R, ghDoc.PreviewColour.G, ghDoc.PreviewColour.B).ToArgb();
      var s = Color
        .FromArgb(ghDoc.PreviewColourSelected.R, ghDoc.PreviewColourSelected.G, ghDoc.PreviewColourSelected.B)
        .ToArgb();
      if (cache.Face != null && cache.PreviewArgb == p && cache.PreviewSelArgb == s)
        return;

      cache.Face?.Dispose();
      cache.FaceSel?.Dispose();
      cache.Face = GH_Material.CreateStandardMaterial(ghDoc.PreviewColour);
      cache.FaceSel = GH_Material.CreateStandardMaterial(ghDoc.PreviewColourSelected);
      cache.PreviewArgb = p;
      cache.PreviewSelArgb = s;
    }

    private static bool CustomMeshMatches(MeshingParameters? cached, MeshingParameters? live) =>
      (cached == null && live == null) || (cached != null && live != null && cached.Equals(live));

    private static void DisposeIfNotDefault(MeshingParameters? mp)
    {
      if (mp != null && !ReferenceEquals(mp, MeshingParameters.Default))
        mp.Dispose();
    }

    /// <summary>
    /// Reuses mesh parameters per document. Skips <see cref="GH_Document.PreviewCurrentMeshParameters"/> when
    /// <see cref="GH_Document.PreviewMeshType"/> and custom settings are unchanged; for <see cref="GH_PreviewMesh.Document"/>,
    /// still queries each frame but keeps a copy when values are equal.
    /// </summary>
    private static MeshingParameters GetCachedMeshingParameters(GH_Document ghDoc, FollowPreviewCache cache)
    {
      var type = ghDoc.PreviewMeshType;
      var customLive = type == GH_PreviewMesh.Custom ? ghDoc.PreviewCustomMeshParameters : null;

      var structuralChange = cache.MeshCopy == null
        || cache.MeshType != type
        || (type == GH_PreviewMesh.Custom && !CustomMeshMatches(cache.CustomMeshSnap, customLive));

      if (structuralChange)
      {
        cache.MeshCopy?.Dispose();
        cache.CustomMeshSnap?.Dispose();
        cache.CustomMeshSnap = null;

        var live = ghDoc.PreviewCurrentMeshParameters() ?? MeshingParameters.Default;
        cache.MeshCopy = new MeshingParameters(live);
        DisposeIfNotDefault(live);
        cache.MeshType = type;
        if (type == GH_PreviewMesh.Custom && customLive != null)
          cache.CustomMeshSnap = new MeshingParameters(customLive);
        return cache.MeshCopy;
      }

      if (type == GH_PreviewMesh.Document)
      {
        var live = ghDoc.PreviewCurrentMeshParameters() ?? MeshingParameters.Default;
        if (cache.MeshCopy!.Equals(live))
        {
          DisposeIfNotDefault(live);
          return cache.MeshCopy;
        }

        cache.MeshCopy.Dispose();
        cache.MeshCopy = new MeshingParameters(live);
        DisposeIfNotDefault(live);
        return cache.MeshCopy;
      }

      return cache.MeshCopy!;
    }
  }
}
