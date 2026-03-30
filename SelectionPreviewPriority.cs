using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SelectionPreview
{
  /// <summary>
  /// Toolbar toggle: while enabled, selected <see cref="IGH_PreviewObject"/> instances that are
  /// <see cref="IGH_PreviewObject.Hidden"/> still draw in the Rhino viewport (Grasshopper normally
  /// skips them when preview filter is <see cref="GH_PreviewFilter.None"/>). <c>Hidden</c> is never
  /// modified, so copy/paste and Preview On/Off behave normally.
  /// </summary>
  public class SelectionPreviewPriority : GH_AssemblyPriority
  {
    private const string ButtonName = "SelPreviewBtn";
    private const string SepName    = "SelPreviewSep";

    private static bool _featureEnabled;
    private static ToolStripButton? _toolButton;
    private static bool _toolbarRegistered;
    private static bool _toolbarRegisterPending;

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
        ToolTipText   = "Follow selection — show viewport preview for selected hidden objects (does not change Preview On/Off).",
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

      editor.BeginInvoke(new Action(() =>
      {
        if (_toolButton == null || _toolButton.IsDisposed) return;
        var old = _toolButton.Image;
        _toolButton.Image = SelectionPreviewIcons.MakeIcon(_featureEnabled);
        old?.Dispose();
        _toolButton.Invalidate();
        toolbar.Invalidate();
      }));

      FollowSelectionViewportConduit.Instance.Enabled = _featureEnabled;
      FollowSelectionViewportConduit.FeatureEnabled   = _featureEnabled;

      return true;
    }

    private static void ToggleFeature()
    {
      _featureEnabled = !_featureEnabled;
      FollowSelectionViewportConduit.FeatureEnabled   = _featureEnabled;
      FollowSelectionViewportConduit.Instance.Enabled = _featureEnabled;

      if (_toolButton != null)
      {
        _toolButton.Checked = _featureEnabled;
        var old = _toolButton.Image;
        _toolButton.Image = SelectionPreviewIcons.MakeIcon(_featureEnabled);
        old?.Dispose();
      }

      Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
      Instances.ActiveCanvas?.Refresh();
    }
  }
}
