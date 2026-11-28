using MultiUserEdit.Commons.Events;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons.Models
{
    public sealed class ScriptEntry : Bindable
    {
        public ScriptEntry(ScriptItem item)
        {
            ScriptId = item.ScriptId;
            AuthorName = item.AuthorName ?? string.Empty;
            AuthorId = item.AuthorId;
            CreatedAt = item.CreatedAt;
            IsShared = true;
            CharacterName = item.CharacterName;
            Serif = item.Serif;
        }

        public ScriptEntry(string characterName)
        {
            ScriptId = Guid.NewGuid();
            AuthorName = string.Empty;
            CreatedAt = DateTime.UtcNow;
            CharacterName = characterName;
        }

        public Guid ScriptId { get; }
        public string AuthorName { get; }
        public Guid AuthorId { get; }
        public DateTime CreatedAt { get; }
        public bool IsShared { get; }

        private string characterName = string.Empty;
        public string CharacterName { get => characterName; set => Set(ref characterName, value); }
        private string serif = string.Empty;
        public string Serif { get => serif; set => Set(ref serif, value); }

        public ScriptItem ToItem() => new(ScriptId, CharacterName, Serif, AuthorName, AuthorId, CreatedAt);
    }
}
