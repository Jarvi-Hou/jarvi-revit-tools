using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace JarviTools.Commands.MaintenanceReachability
{
    /// <summary>
    /// Process-local monotonic document revision. Element VersionGuid catches edits
    /// to known evidence; this serial also invalidates a snapshot when a new element
    /// is added after the snapshot was created.
    /// </summary>
    internal static class MaintenanceDocumentChangeTracker
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, long> Revisions =
            new Dictionary<int, long>();

        internal static long GetSerial(Document document)
        {
            if (document == null) return -1L;
            int key = document.GetHashCode();
            lock (Gate)
            {
                long value;
                if (!Revisions.TryGetValue(key, out value))
                {
                    value = 0L;
                    Revisions[key] = value;
                }
                return value;
            }
        }

        internal static void TrackOpened(Document document)
        {
            if (document == null) return;
            lock (Gate) Revisions[document.GetHashCode()] = 0L;
        }

        internal static void OnDocumentChanged(
            object sender,
            DocumentChangedEventArgs args)
        {
            if (args == null) return;
            Document document;
            try { document = args.GetDocument(); }
            catch { return; }
            if (document == null) return;
            int key = document.GetHashCode();
            lock (Gate)
            {
                long value;
                Revisions.TryGetValue(key, out value);
                Revisions[key] = value == long.MaxValue ? 1L : value + 1L;
            }
        }

        internal static void Clear()
        {
            lock (Gate) Revisions.Clear();
        }
    }
}
