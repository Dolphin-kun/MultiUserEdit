using MultiUserEdit.ViewModels;
using MultiUserEdit.Views;
using System.Reflection;
using YukkuriMovieMaker.Plugin;

namespace MultiUserEdit
{
    [PluginDetails(AuthorName ="いるかぁぁ",ContentId = "nc505669")]
    public class MultiUserEditPlugin : IToolPlugin
    {
        public const string PluginName = "共同編集";

        public string Name => PluginName;
        public Type ViewModelType => typeof(MultiUserEditViewModel);
        public Type ViewType => typeof(MultiUserEditView);

        public PluginDetailsAttribute Details => GetType().GetCustomAttribute<PluginDetailsAttribute>() ?? new();
    }
}
