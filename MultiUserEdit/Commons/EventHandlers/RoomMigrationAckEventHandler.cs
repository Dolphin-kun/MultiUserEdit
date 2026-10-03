using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal class RoomMigrationAckEventHandler : IClientEventHandler<RoomMigrationAckEvent>
    {
        public void Handle(RoomMigrationAckEvent editEvent, MultiUserEditViewModel viewModel)
        {
            viewModel.HandleRoomMigrationAck(editEvent);
        }
    }
}
