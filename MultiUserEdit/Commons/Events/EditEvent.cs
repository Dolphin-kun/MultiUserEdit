using System.Text.Json.Serialization;

namespace MultiUserEdit.Commons.Events
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(SyncScenesEvent), 10)]
    [JsonDerivedType(typeof(ItemMovedEvent), 11)]
    [JsonDerivedType(typeof(ItemAddedEvent), 12)]
    [JsonDerivedType(typeof(ItemRemovedEvent), 13)]
    [JsonDerivedType(typeof(ItemUpdatedEvent), 14)]
    [JsonDerivedType(typeof(PresenceEvent), 15)]
    [JsonDerivedType(typeof(SyncRequestEvent), 16)]
    [JsonDerivedType(typeof(ItemLockedEvent), 17)]
    [JsonDerivedType(typeof(ItemUnlockedEvent), 18)]
    [JsonDerivedType(typeof(CursorMovedEvent), 19)]
    [JsonDerivedType(typeof(FileTransferStartEvent), 20)]
    [JsonDerivedType(typeof(FileChunkEvent), 21)]
    [JsonDerivedType(typeof(SceneAddedEvent), 22)]
    [JsonDerivedType(typeof(SceneRemovedEvent), 23)]
    [JsonDerivedType(typeof(PermissionUpdatedEvent), 24)]
    [JsonDerivedType(typeof(UserLeftEvent), 25)]
    [JsonDerivedType(typeof(UserKickedEvent), 26)]
    [JsonDerivedType(typeof(SceneRenamedEvent), 27)]
    [JsonDerivedType(typeof(VideoInfoUpdatedEvent), 28)]
    public abstract record EditEvent
    {
        public DateTime DateTime { get; init; }
        public Guid ExecutorId  { get; init; }
    }
}
