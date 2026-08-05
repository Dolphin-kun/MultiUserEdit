using MultiUserEdit.ViewModels;
using MultiUserEdit.Views;
using YukkuriMovieMaker.Plugin;

namespace MultiUserEdit
{
    public class MultiUserEditPlugin : IToolPlugin
    {
        public const string PluginName = "共同編集";

        public string Name => PluginName;
        public Type ViewModelType => typeof(MultiUserEditViewModel);
        public Type ViewType => typeof(MultiUserEditView);
    }
}
