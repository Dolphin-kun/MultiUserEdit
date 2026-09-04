using System.ComponentModel;

namespace MultiUserEdit.Commons.Models
{
    public sealed class TimelineSubscription(PropertyChangedEventHandler timelineHandler, PropertyChangedEventHandler? videoInfoHandler)
    {
        public PropertyChangedEventHandler TimelineHandler { get; } = timelineHandler;
        public PropertyChangedEventHandler? VideoInfoHandler { get; } = videoInfoHandler;
    }
}
