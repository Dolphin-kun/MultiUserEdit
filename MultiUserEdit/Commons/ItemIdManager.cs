using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Project.Items;

namespace MultiUserEdit.Commons
{
    internal static class ItemIdManager
    {
        private static readonly ConditionalWeakTable<IItem, GuidHolder> table = [];

        private class GuidHolder { public Guid Id { get; set; } }

        public static Guid GetOrCreateId(IItem item)
        {
            return table.GetValue(item, _ => new GuidHolder { Id = Guid.NewGuid() }).Id;
        }

        public static void RegisterId(IItem item, Guid id)
        {
            var holder = table.GetOrCreateValue(item);
            holder.Id = id;
        }
    }
}
