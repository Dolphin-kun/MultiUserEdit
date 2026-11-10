using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal static class SceneResolver
    {
        public static void ResolveSceneId(IItem item, Scenes? scenes)
        {
            if (scenes == null || item is not SceneItem sceneItem) return;

            if (sceneItem.SceneId == Guid.Empty) return;

            var currentSceneId = SceneIdMap.ToLocal(sceneItem.SceneId);

            var matchingTimelineById = scenes.Timelines.FirstOrDefault(t => t.ID == currentSceneId);
            if (matchingTimelineById != null)
            {
                if (sceneItem.SceneId != currentSceneId) sceneItem.SceneId = currentSceneId;
                return;
            }

            var fallbackTimeline = scenes.Timelines.FirstOrDefault();
            if (fallbackTimeline != null)
            {
                sceneItem.SceneId = fallbackTimeline.ID;
            }
        }
    }
}
