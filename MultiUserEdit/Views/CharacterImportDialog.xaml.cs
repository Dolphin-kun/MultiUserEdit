using System.Windows;
using System.Windows.Data;
using YukkuriMovieMaker.Project;

namespace MultiUserEdit.Views
{
    public partial class CharacterImportDialog : Window
    {
        public Character? LinkedCharacter { get; private set; }

        public CharacterImportDialog(string characterName, string ownerName, IReadOnlyList<Character> characters, bool canCreate = true)
        {
            InitializeComponent();

            CharacterNameText.Text = $"「{characterName}」";
            OwnerText.Text = string.IsNullOrEmpty(ownerName) ? string.Empty : string.Format(Texts.FromOwner, ownerName);

            var view = new CollectionViewSource { Source = characters };
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Character.GroupName)));
            CharacterCombo.ItemsSource = view.View;

            var sameName = characters.FirstOrDefault(c => c.Name == characterName);
            CharacterCombo.SelectedItem = sameName ?? (characters.Count > 0 ? characters[0] : null);

            if (!canCreate)
            {
                CreateRadio.IsEnabled = false;
                CreateHint.Text = Texts.CannotCreateCharacter;
            }
            else if (characters.Count == 0)
            {
                LinkRadio.IsEnabled = false;
                CreateRadio.IsChecked = true;
            }
        }

        private void OnDecideClick(object sender, RoutedEventArgs e)
        {
            LinkedCharacter = LinkRadio.IsChecked == true ? CharacterCombo.SelectedItem as Character : null;
            DialogResult = true;
        }
    }
}
