using System.ComponentModel;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons.Models
{
    public sealed class ItemSubscription(IItem item, PropertyChangedEventHandler handler)
    {
        public IItem Item { get; } = item;
        public PropertyChangedEventHandler Handler { get; } = handler;
    }
}
