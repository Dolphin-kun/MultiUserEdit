using MultiUserEdit.ViewModels;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiUserEdit.Commons
{
    internal static class PlaybackStateWatcher
    {
        private const string PlayingPropertyName = "IsPlaying";

        private static readonly ConditionalWeakTable<object, Watcher> watchers = [];

        public static void Watch(object? previewViewModel, MultiUserEditViewModel viewModel)
        {
            if (previewViewModel is not INotifyPropertyChanged notifier) return;

            lock (watchers)
            {
                if (watchers.TryGetValue(previewViewModel, out var existing))
                {
                    existing.Retarget(viewModel);
                    return;
                }

                var watcher = new Watcher(viewModel);
                notifier.PropertyChanged += watcher.OnPropertyChanged;
                watchers.Add(previewViewModel, watcher);
            }
        }

        private sealed class Watcher(MultiUserEditViewModel viewModel)
        {
            private WeakReference<MultiUserEditViewModel> target = new(viewModel);

            public void Retarget(MultiUserEditViewModel viewModel) => target = new(viewModel);

            public void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != PlayingPropertyName) return;
                if (target.TryGetTarget(out var viewModel)) viewModel.NotifyPlaybackStateChanged();
            }
        }
    }
}
