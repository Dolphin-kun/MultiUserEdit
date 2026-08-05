using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal static class SceneResolver
    {
        public static void ResolveSceneId(IItem item, Scenes? scenes)
        {
            if (scenes == null || item is not SceneItem sceneItem) return;

            var currentSceneId = sceneItem.SceneId;
            if (currentSceneId == Guid.Empty) return;

            var matchingTimelineById = scenes.Timelines.FirstOrDefault(t => t.ID == currentSceneId);
            if (matchingTimelineById != null) return;

            var fallbackTimeline = scenes.Timelines.FirstOrDefault();
            if (fallbackTimeline != null)
            {
                sceneItem.SceneId = fallbackTimeline.ID;
            }
        }
    }
}
