using System.Reflection;
using System.Windows;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Views;

namespace MultiUserEdit.Commons
{
    internal static class MainViewLocator
    {
        public static Window? Find(Scenes? scenes)
        {
            if (scenes == null || Application.Current == null) return null;

            foreach (Window window in Application.Current.Windows)
            {
                if (window is not MainView) continue;
                var context = window.DataContext;
                var model = context?.GetType().GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(context);
                if (ReferenceEquals(model?.GetType().GetProperty("Scenes")?.GetValue(model), scenes)) return window;
            }
            return null;
        }
    }
}
