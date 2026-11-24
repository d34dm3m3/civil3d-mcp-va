using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Remembers the user's latest pick-first selection per drawing. The plugin runs its work in its own
/// command, which clears the pick-first set before Editor.SelectImplied can read it (selected_objects_info
/// always came back empty, 2026-10-06); the selection is therefore captured when it changes.
/// </summary>
internal static class SelectionTracker
{
  private static readonly object Sync = new();
  private static readonly Dictionary<Document, ObjectId[]> Latest = new();

  // C70: the handlers are kept so Stop can detach them when the plugin is reloaded without restarting Civil 3D.
  private static readonly Dictionary<Document, EventHandler> Handlers = new();
  private static DocumentCollectionEventHandler? _created;
  private static DocumentCollectionEventHandler? _destroyed;

  public static void Start()
  {
    foreach (Document doc in App.DocumentManager) Attach(doc);
    _created = (_, e) => Attach(e.Document);
    _destroyed = (_, e) =>
    {
      lock (Sync)
      {
        Latest.Remove(e.Document);
        Handlers.Remove(e.Document);
      }
    };
    App.DocumentManager.DocumentCreated += _created;
    App.DocumentManager.DocumentToBeDestroyed += _destroyed;
  }

  /// <summary>Detaches every handler (plugin reload, C70).</summary>
  public static void Stop()
  {
    if (_created != null) App.DocumentManager.DocumentCreated -= _created;
    if (_destroyed != null) App.DocumentManager.DocumentToBeDestroyed -= _destroyed;
    _created = null;
    _destroyed = null;
    lock (Sync)
    {
      foreach (var (doc, handler) in Handlers)
      {
        try
        {
          doc.ImpliedSelectionChanged -= handler;
        }
        catch (System.Exception)
        {
          // the document is closing
        }
      }

      Handlers.Clear();
      Latest.Clear();
    }
  }

  private static void Attach(Document? doc)
  {
    if (doc == null) return;
    EventHandler handler = (_, _) =>
    {
      try
      {
        var result = doc.Editor.SelectImplied();
        var ids = result.Status == PromptStatus.OK && result.Value != null ? result.Value.GetObjectIds() : Array.Empty<ObjectId>();
        // An empty pick-first set also happens when the plugin's own command starts; keep the last
        // non-empty selection so it can still be read.
        if (ids.Length == 0) return;
        lock (Sync) Latest[doc] = ids;
      }
      catch (System.Exception ex)
      {
        PluginLog.Error("SelectionTracker", "Could not read the implied selection", ex);
      }
    };
    lock (Sync)
    {
      if (Handlers.ContainsKey(doc)) return;
      Handlers[doc] = handler;
    }
    doc.ImpliedSelectionChanged += handler;
  }

  public static ObjectId[] For(Document doc)
  {
    lock (Sync) return Latest.TryGetValue(doc, out var ids) ? ids.Where(id => !id.IsNull && !id.IsErased).ToArray() : Array.Empty<ObjectId>();
  }
}
