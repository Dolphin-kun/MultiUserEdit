using System.Diagnostics;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.UndoRedo;

namespace MultiUserEdit.Commons
{
    internal static class SceneTabRefresher
    {
        private sealed class Marker : IUndoRedoCommandBase
        {
            public bool IsAction => false;
            public bool IsEmpty => false;
            public bool CanConnect(IUndoRedoCommandBase command) => command is Marker;
            public IUndoRedoCommandBase Connect(IUndoRedoCommandBase command) => this;
        }

        public static bool ShowsSceneTabs(Scenes? scenes) =>
            scenes != null
            && (scenes.Timelines.Count > 1
                || scenes.Timelines.Any(timeline => timeline.Items.Any(item => item is SceneItem)));

        public static void Refresh(UndoRedoManager? undoRedoManager)
        {
            if (undoRedoManager == null) return;

            try
            {
                undoRedoManager.AddCommand(new Marker());
                undoRedoManager.Record();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiUserEdit] scene tab refresh failed: {ex.Message}");
            }
        }
    }
}
