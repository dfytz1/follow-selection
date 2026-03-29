using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SelectionPreview
{
  /// <summary>
  /// Adds a toolbar toggle: while enabled, selected <see cref="IGH_PreviewObject"/> instances
  /// are forced visible; on deselect, original <see cref="IGH_PreviewObject.Hidden"/> is restored.
  /// Selection is tracked via <see cref="GH_Canvas.CanvasPostPaintObjects"/> and a per-document
  /// <see cref="HashSet{Guid}"/> diff (no public selection-changed event in GH).
  /// </summary>
  public class SelectionPreviewPriority : GH_AssemblyPriority
  {
    private const string ButtonName = "SelPreviewBtn";
    private const string SepName    = "SelPreviewSep";

    private static bool _featureEnabled;
    private static ToolStripButton? _toolButton;
    private static bool _pendingRefresh;
    private static bool _toolbarRegistered;
    private static bool _toolbarRegisterPending;

    /// <summary>Per-document: GUID → original Hidden before we forced show.</summary>
    private static readonly Dictionary<GH_Document, Dictionary<Guid, bool>> SavedHiddenByDoc = new();

    /// <summary>Per-document: last frame's selected object GUIDs.</summary>
    private static readonly Dictionary<GH_Document, HashSet<Guid>> LastSelectionByDoc = new();

    private static readonly HashSet<GH_Canvas> HookedCanvases = new();

    public override GH_LoadingInstruction PriorityLoad()
    {
      Instances.CanvasCreated += OnCanvasCreated;
      if (Instances.ActiveCanvas != null)
        HookCanvas(Instances.ActiveCanvas);
      return GH_LoadingInstruction.Proceed;
    }

    private static void OnCanvasCreated(GH_Canvas canvas) => HookCanvas(canvas);

    private static void HookCanvas(GH_Canvas? canvas)
    {
      if (canvas == null || !HookedCanvases.Add(canvas)) return;

      canvas.CanvasPostPaintObjects += OnCanvasPostPaint;
      canvas.DocumentChanged       += OnDocumentChanged;

      EnsureToolbarOnEditor();
    }

    private static void EnsureToolbarOnEditor()
    {
      if (_toolbarRegistered || _toolbarRegisterPending) return;

      var editor = Instances.DocumentEditor;
      if (editor == null) return;

      _toolbarRegisterPending = true;

      void register()
      {
        try
        {
          if (_toolbarRegistered) return;
          if (TryAddToolbarButton())
            _toolbarRegistered = true;
        }
        finally
        {
          _toolbarRegisterPending = false;
        }
      }

      try
      {
        if (editor.IsHandleCreated)
          editor.BeginInvoke(new Action(register));
        else
        {
          EventHandler? load = null;
          load = (_, _) =>
          {
            editor.Load -= load;
            register();
          };
          editor.Load += load;
        }
      }
      catch
      {
        register();
      }
    }

    /// <summary>
    /// Returns the index at which to insert our button (after the built-in preview cluster), or -1 if no anchor matched.
    /// Names are not public API and may differ by Grasshopper version; <see cref="ToolStripItemAlignment.Right"/> is the real fallback.
    /// </summary>
    private static int FindPreviewClusterInsertIndex(ToolStrip toolbar)
    {
      var lastAnchor = -1;
      for (var i = 0; i < toolbar.Items.Count; i++)
      {
        var n = toolbar.Items[i].Name ?? string.Empty;
        foreach (var a in PreviewClusterAnchorNames)
        {
          if (n == a)
          {
            lastAnchor = i;
            break;
          }
        }
      }

      return lastAnchor >= 0 ? lastAnchor + 1 : -1;
    }

    /// <summary>Known canvas-toolbar item names for the preview/display cluster (best-effort).</summary>
    private static readonly string[] PreviewClusterAnchorNames =
    {
      "PreviewMeshButton",
      "PreviewCurveButton",
      "PreviewPointButton",
      "PreviewFloatingPointParamBtn",
      "WireframeModeButton",
      "PreviewModeDropDown",
      "CanvasBackgroundButton",
    };

    private static bool TryAddToolbarButton()
    {
      var editor = Instances.DocumentEditor;
      if (editor == null || editor.Controls.Count < 1) return false;
      var panel = editor.Controls[0];
      if (panel.Controls.Count < 2) return false;

      var toolbar = panel.Controls[1] as ToolStrip;
      if (toolbar == null) return false;

      for (var i = toolbar.Items.Count - 1; i >= 0; i--)
      {
        var n = toolbar.Items[i].Name;
        if (n == ButtonName || n == SepName)
          toolbar.Items.RemoveAt(i);
      }

      _toolButton = new ToolStripButton
      {
        Name          = ButtonName,
        ToolTipText   = "Follow selection — show viewport preview while selected; restore Hidden when deselected.",
        DisplayStyle  = ToolStripItemDisplayStyle.Image,
        Image         = SelectionPreviewIcons.MakeIcon(_featureEnabled),
        CheckOnClick  = false,
        Checked       = _featureEnabled,
        Alignment     = ToolStripItemAlignment.Right,
        ImageScaling  = ToolStripItemImageScaling.None,
        AutoSize      = false,
        Size          = new Size(28, 22),
      };
      _toolButton.Click += (_, _) => ToggleFeature();

      var insertAt = FindPreviewClusterInsertIndex(toolbar);
      if (insertAt < 0)
      {
        var lastSep = -1;
        for (var i = 0; i < toolbar.Items.Count; i++)
        {
          if (toolbar.Items[i] is ToolStripSeparator)
            lastSep = i;
        }

        if (lastSep < 0)
          toolbar.Items.Add(_toolButton);
        else
          toolbar.Items.Insert(lastSep + 1, _toolButton);
      }
      else
      {
        if (insertAt > 0 && toolbar.Items[insertAt - 1] is not ToolStripSeparator)
        {
          toolbar.Items.Insert(insertAt, new ToolStripSeparator
          {
            Name      = SepName,
            Alignment = ToolStripItemAlignment.Right,
          });
          insertAt++;
        }

        toolbar.Items.Insert(insertAt, _toolButton);
      }

      toolbar.PerformLayout();
      _toolButton.Invalidate();
      toolbar.Invalidate();

      // One deferred pass: fixes cases where the strip hasn't laid out handles yet on first registration.
      editor.BeginInvoke(new Action(() =>
      {
        if (_toolButton == null || _toolButton.IsDisposed) return;
        var old = _toolButton.Image;
        _toolButton.Image = SelectionPreviewIcons.MakeIcon(_featureEnabled);
        old?.Dispose();
        _toolButton.Invalidate();
        toolbar.Invalidate();
      }));

      return true;
    }

    private static void ToggleFeature()
    {
      _featureEnabled = !_featureEnabled;
      if (_toolButton != null)
      {
        _toolButton.Checked = _featureEnabled;
        var old = _toolButton.Image;
        _toolButton.Image = SelectionPreviewIcons.MakeIcon(_featureEnabled);
        old?.Dispose();
      }

      if (!_featureEnabled)
      {
        _pendingRefresh = false;
        foreach (var doc in SavedHiddenByDoc.Keys.ToList())
          RestoreAll(doc);
        SavedHiddenByDoc.Clear();
        LastSelectionByDoc.Clear();
        Instances.ActiveCanvas?.Document?.NewSolution(false);
        Instances.ActiveCanvas?.Refresh();
      }
    }

    private static Dictionary<Guid, bool> GetSaved(GH_Document doc)
    {
      if (!SavedHiddenByDoc.TryGetValue(doc, out var map))
      {
        map = new Dictionary<Guid, bool>();
        SavedHiddenByDoc[doc] = map;
      }
      return map;
    }

    private static HashSet<Guid> GetLastSel(GH_Document doc)
    {
      if (!LastSelectionByDoc.TryGetValue(doc, out var set))
      {
        set = new HashSet<Guid>();
        LastSelectionByDoc[doc] = set;
      }
      return set;
    }

    private static void OnCanvasPostPaint(GH_Canvas sender)
    {
      if (!_toolbarRegistered)
        EnsureToolbarOnEditor();

      if (!_featureEnabled || _pendingRefresh) return;

      var doc = sender.Document;
      if (doc == null) return;

      var current = new HashSet<Guid>();
      foreach (var obj in doc.Objects)
      {
        if (obj.Attributes.Selected)
          current.Add(obj.InstanceGuid);
      }

      var lastSelection = GetLastSel(doc);
      if (current.SetEquals(lastSelection)) return;

      var savedHidden = GetSaved(doc);
      var dirty       = false;

      // Newly deselected → restore saved Hidden (or drop stale entry if object gone)
      foreach (var id in lastSelection)
      {
        if (current.Contains(id)) continue;

        if (!savedHidden.TryGetValue(id, out var wasHidden))
          continue;

        savedHidden.Remove(id);
        var obj = doc.FindObject(id, false);
        if (obj is IGH_PreviewObject pObj)
        {
          pObj.Hidden = wasHidden;
          dirty       = true;
        }
      }

      // Newly selected → remember Hidden, force show
      foreach (var id in current)
      {
        if (lastSelection.Contains(id)) continue;

        var obj = doc.FindObject(id, false);
        if (obj is not IGH_PreviewObject pObj) continue;

        if (!savedHidden.ContainsKey(id))
          savedHidden[id] = pObj.Hidden;

        if (pObj.Hidden)
        {
          pObj.Hidden = false;
          dirty       = true;
        }
      }

      lastSelection.Clear();
      foreach (var g in current) lastSelection.Add(g);

      if (dirty)
      {
        _pendingRefresh = true;
        sender.BeginInvoke(new Action(() =>
        {
          _pendingRefresh = false;
          Instances.ActiveCanvas?.Document?.NewSolution(false);
        }));
      }
    }

    private static void OnDocumentChanged(GH_Canvas sender, GH_CanvasDocumentChangedEventArgs e)
    {
      if (e.OldDocument != null)
      {
        RestoreAll(e.OldDocument);
        SavedHiddenByDoc.Remove(e.OldDocument);
        LastSelectionByDoc.Remove(e.OldDocument);
      }
    }

    private static void RestoreAll(GH_Document? doc)
    {
      if (doc == null)
      {
        SavedHiddenByDoc.Clear();
        return;
      }

      if (!SavedHiddenByDoc.TryGetValue(doc, out var map)) return;

      foreach (var kvp in map)
      {
        var obj = doc.FindObject(kvp.Key, false);
        if (obj is IGH_PreviewObject pObj)
          pObj.Hidden = kvp.Value;
      }

      map.Clear();
    }
  }
}
