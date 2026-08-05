using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons.Models
{
    public class Participant : Bindable
    {
        private Guid userId;
        public Guid UserId { get => userId; set => Set(ref userId, value); }

        private string userName = string.Empty;
        public string UserName { get => userName; set => Set(ref userName, value); }

        private UserRole role;
        public UserRole Role { get => role; set => Set(ref role, value); }

        private DateTime lastActivity;
        public DateTime LastActivity { get => lastActivity; set => Set(ref lastActivity, value); }

        private UserStatus status;
        public UserStatus Status
        {
            get => status;
            set
            {
                if (Set(ref status, value))
                {
                    OnPropertyChanged(nameof(StatusBrush));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public Brush StatusBrush => Status switch
        {
            UserStatus.Active => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            UserStatus.Away => new SolidColorBrush(Color.FromRgb(255, 152, 0)),
            _ => new SolidColorBrush(Color.FromRgb(158, 158, 158))
        };

        public string StatusText => Status switch
        {
            UserStatus.Active => "オンライン",
            UserStatus.Away => "離席中",
            _ => "オフライン"
        };

        private int currentFrame;
        public int CurrentFrame { get => currentFrame; set => Set(ref currentFrame, value); }

        private int currentTimelineIndex;
        public int CurrentTimelineIndex { get => currentTimelineIndex; set => Set(ref currentTimelineIndex, value); }

        private Color themeColor;
        public Color ThemeColor { get => themeColor; set => Set(ref themeColor, value); }

        private UserPermission permission = new();
        public UserPermission Permission { get => permission; set => Set(ref permission, value); }
    }
}
