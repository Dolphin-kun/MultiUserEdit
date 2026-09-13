using System.Collections;
using System.Diagnostics;
using System.Reflection;
using YukkuriMovieMaker.UndoRedo;

namespace MultiUserEdit.Commons
{
    internal static class UndoRecordSuppressor
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo? CollectorField =
            typeof(UndoRedoManager).GetField("collector", InstanceMembers);

        private static readonly FieldInfo? CommandsField =
            CollectorField?.FieldType.GetField("commands", InstanceMembers);

        public static bool IsAvailable => CollectorField != null && CommandsField != null;

        public readonly struct Scope(UndoRedoManager? manager, object? collector, List<object>? snapshot) : IDisposable
        {
            public void Dispose()
            {
                if (manager == null || collector == null || snapshot == null) return;

                try
                {
                    if (!ReferenceEquals(CollectorField!.GetValue(manager), collector)) return;
                    if (CommandsField!.GetValue(collector) is not IList commands) return;

                    lock (commands)
                    {
                        commands.Clear();
                        foreach (var command in snapshot) commands.Add(command);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MultiUserEdit] UndoRecordSuppressor restore failed: {ex.Message}");
                }
            }
        }

        public static Scope Suppress(UndoRedoManager? manager)
        {
            if (manager == null || !IsAvailable) return new Scope(null, null, null);

            try
            {
                var collector = CollectorField!.GetValue(manager);
                if (collector == null) return new Scope(null, null, null);
                if (CommandsField!.GetValue(collector) is not IList commands) return new Scope(null, null, null);

                List<object> snapshot;
                lock (commands)
                {
                    snapshot = new List<object>(commands.Count);
                    foreach (var command in commands)
                    {
                        if (command != null) snapshot.Add(command);
                    }
                }

                return new Scope(manager, collector, snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] UndoRecordSuppressor failed: {ex.Message}");
                return new Scope(null, null, null);
            }
        }
    }
}
