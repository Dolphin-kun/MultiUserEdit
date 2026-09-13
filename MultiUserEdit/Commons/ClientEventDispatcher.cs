using MultiUserEdit.Commons.EventHandlers;
using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons
{
    internal class ClientEventDispatcher
    {
        private readonly Dictionary<Type, IClientEventHandler> handlers = [];

        public ClientEventDispatcher()
        {
            Register(new SyncScenesEventHandler());
            Register(new ItemMovedEventHandler());
            Register(new ItemAddedEventHandler());
            Register(new ItemRemovedEventHandler());
            Register(new ItemUpdatedEventHandler());
            Register(new PresenceEventHandler());
            Register(new SyncRequestEventHandler());
            Register(new ItemLockedEventHandler());
            Register(new ItemUnlockedEventHandler());
            Register(new CursorMovedEventHandler());
            Register(new FileAvailableEventHandler());
            Register(new FileRequestEventHandler());
            Register(new FileTransferStartEventHandler());
            Register(new FileChunkEventHandler());
            Register(new CharacterRequestEventHandler());
            Register(new CharacterSharedEventHandler());
            Register(new SceneAddedEventHandler());
            Register(new SceneRemovedEventHandler());
            Register(new SceneRenamedEventHandler());
            Register(new VideoInfoUpdatedEventHandler());
            Register(new ItemStateRequestEventHandler());
            Register(new PermissionUpdatedEventHandler());
            Register(new UserLeftEventHandler());
            Register(new UserKickedEventHandler());
        }

        private void Register<TEvent>(IClientEventHandler<TEvent> handler)
            where TEvent : EditEvent
        {
            handlers[typeof(TEvent)] = handler;
        }

        public void Dispatch(EditEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (handlers.TryGetValue(editEvent.GetType(), out var handler))
                handler.Handle(editEvent, viewModel);
        }
    }
}
