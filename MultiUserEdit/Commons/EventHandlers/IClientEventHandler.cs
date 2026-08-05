using MultiUserEdit.Commons.Events;
using MultiUserEdit.ViewModels;

namespace MultiUserEdit.Commons.EventHandlers
{
    internal interface IClientEventHandler
    {
        void Handle(EditEvent editEvent, MultiUserEditViewModel viewModel);
    }

    internal interface IClientEventHandler<TEvent> : IClientEventHandler
        where TEvent : EditEvent
    {
        void Handle(TEvent editEvent, MultiUserEditViewModel viewModel);

        void IClientEventHandler.Handle(EditEvent editEvent, MultiUserEditViewModel viewModel)
        {
            if (editEvent is TEvent typedEvent)
            {
                Handle(typedEvent, viewModel);
            }
        }
    }
}
