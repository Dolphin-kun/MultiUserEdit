using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class RoomMigrationEventHandler : IClientEventHandler<RoomMigrationEvent>
    {
        public void Handle(RoomMigrationEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleRoomMigration(editEvent);
        }
    }
}
