using System.Windows;

namespace MultiUserEdit.Views
{
    public partial class CharacterNameDialog : Window
    {
        private readonly HashSet<string> existingNames;

        public string CharacterName => NameBox.Text.Trim();

        public CharacterNameDialog(string suggestedName, IEnumerable<string> existingNames)
        {
            InitializeComponent();

            this.existingNames = new HashSet<string>(existingNames, StringComparer.Ordinal);

            NameBox.Text = suggestedName;
            NameBox.SelectAll();
            NameBox.Focus();
        }

        private void OnNameChanged(object sender, RoutedEventArgs e)
        {
            var name = CharacterName;
            var isDuplicate = existingNames.Contains(name);

            ErrorText.Visibility = isDuplicate ? Visibility.Visible : Visibility.Collapsed;
            DecideButton.IsEnabled = name.Length > 0 && !isDuplicate;
        }

        private void OnDecideClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
