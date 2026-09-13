using MultiUserEdit.Commons.Models;
using MultiUserEdit.Settings;
using MultiUserEdit.Views.Converters;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MultiUserEdit.Views.SettingsPages
{
    public partial class FileSettingsView : UserControl
    {
        private readonly ObservableCollection<ExtensionOption> extensionOptions = [];

        private bool suppressSave;

        public FileSettingsView()
        {
            Resources.Add("EnumToBoolConverter", new EnumToBoolConverter());
            InitializeComponent();

            var view = new CollectionViewSource { Source = extensionOptions };
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ExtensionOption.GroupName)));
            ExtensionList.ItemsSource = view.View;

            LoadOptions();
        }

        private void LoadOptions()
        {
            var enabled = ParseSettings();

            extensionOptions.Clear();

            foreach (var extension in FileExtensionCatalog.AllExtensions)
                AddOption(extension, enabled.Contains(extension));

            foreach (var extension in enabled.Except(FileExtensionCatalog.AllExtensions))
                AddOption(extension, true);
        }

        private void AddOption(string extension, bool isEnabled)
        {
            var option = new ExtensionOption(extension, isEnabled);
            option.Changed += Save;
            extensionOptions.Add(option);
        }

        private static HashSet<string> ParseSettings()
        {
            return [.. MultiUserEditSettings.Default.AllowedExtensions
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(FileExtensionCatalog.Normalize)
                .Where(extension => extension != null)
                .Select(extension => extension!)];
        }

        private void Save()
        {
            if (suppressSave) return;

            MultiUserEditSettings.Default.AllowedExtensions = string.Join(", ",
                extensionOptions.Where(option => option.IsEnabled).Select(option => option.Extension));
        }

        private void SetAll(bool isEnabled)
        {
            suppressSave = true;
            foreach (var option in extensionOptions) option.SetEnabledSilently(isEnabled);
            suppressSave = false;
            Save();
        }

        private void OnCheckAllClick(object sender, RoutedEventArgs e) => SetAll(true);

        private void OnUncheckAllClick(object sender, RoutedEventArgs e) => SetAll(false);

        private void OnResetToDefaultClick(object sender, RoutedEventArgs e)
        {
            MultiUserEditSettings.Default.AllowedExtensions = FileExtensionCatalog.DefaultCsv;
            LoadOptions();
        }

        private void OnAddExtensionClick(object sender, RoutedEventArgs e) => AddTypedExtension();

        private void OnNewExtensionKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            e.Handled = true;
            AddTypedExtension();
        }

        private void AddTypedExtension()
        {
            var extension = FileExtensionCatalog.Normalize(NewExtensionBox.Text);
            NewExtensionBox.Text = string.Empty;
            if (extension == null) return;

            var existing = extensionOptions.FirstOrDefault(option => option.Extension == extension);
            if (existing != null)
            {
                existing.IsEnabled = true;
                return;
            }

            AddOption(extension, true);
            Save();
        }

        private void OnRemoveExtensionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not ExtensionOption option) return;

            option.Changed -= Save;
            extensionOptions.Remove(option);
            Save();
        }

        private void OnInputPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            Behaviors.MouseWheelRedirector.Redirect(sender, e);
        }

        private void OnNestedScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            Behaviors.MouseWheelRedirector.RedirectAtEdge(sender, e);
        }
    }
}
