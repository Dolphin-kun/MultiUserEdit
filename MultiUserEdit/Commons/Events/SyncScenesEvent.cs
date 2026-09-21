using MultiUserEdit.Commons.Models;

namespace MultiUserEdit.Commons.Events
{
    public record SyncScenesEvent(OnlineScenes Scenes, bool IsManual = false) : EditEvent;
}
