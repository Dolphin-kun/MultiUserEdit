using System.Windows.Media;
using YukkuriMovieMaker.Commons;

namespace MultiUserEdit.Commons.Models
{
    public class Participant : Bindable
    {
        private Guid userId;
        public Guid UserId { get => userId; set => Set(ref userId, value); }

        private Guid profileId;
        public Guid ProfileId { get => profileId; set => Set(ref profileId, value); }

        private string userName = string.Empty;
        public string UserName { get => userName; set => Set(ref userName, value); }

        private string description = string.Empty;
        public string Description { get => description; set => Set(ref description, value); }

        private DateTime joinedAt = DateTime.Now;
        public DateTime JoinedAt { get => joinedAt; set => Set(ref joinedAt, value); }

        private bool hasFileChannel;
        public bool HasFileChannel { get => hasFileChannel; set => Set(ref hasFileChannel, value); }

        private UserRole role;
        public UserRole Role { get => role; set => Set(ref role, value, nameof(Role), nameof(RoleText)); }

        private bool isWeb;
        public bool IsWeb { get => isWeb; set => Set(ref isWeb, value, nameof(IsWeb), nameof(RoleText)); }

        public string RoleText => Role == UserRole.Host ? Texts.Host : IsWeb ? Texts.WebGuest : Texts.Guest;

        private DateTime lastActivity;
        public DateTime LastActivity { get => lastActivity; set => Set(ref lastActivity, value); }

        private UserStatus status;
        public UserStatus Status
        {
            get => status;
            set => Set(ref status, value, nameof(Status), nameof(StatusBrush), nameof(StatusText));
        }

        private static readonly Brush ActiveBrush = Frozen(Color.FromRgb(76, 175, 80));
        private static readonly Brush AwayBrush = Frozen(Color.FromRgb(255, 152, 0));
        private static readonly Brush OfflineBrush = Frozen(Color.FromRgb(158, 158, 158));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public Brush StatusBrush => Status switch
        {
            UserStatus.Active => ActiveBrush,
            UserStatus.Away => AwayBrush,
            _ => OfflineBrush
        };

        public string StatusText => Status switch
        {
            UserStatus.Active => Texts.StatusOnline,
            UserStatus.Away => Texts.StatusAway,
            UserStatus.Disconnected => Texts.StatusDisconnected,
            _ => Texts.StatusOffline
        };

        private int currentFrame;
        public int CurrentFrame { get => currentFrame; set => Set(ref currentFrame, value); }

        private bool isPlaying;
        public bool IsPlaying { get => isPlaying; set => Set(ref isPlaying, value); }

        private DateTime frameUpdatedAt = DateTime.UtcNow;
        public DateTime FrameUpdatedAt { get => frameUpdatedAt; set => Set(ref frameUpdatedAt, value); }

        private int currentTimelineIndex;
        public int CurrentTimelineIndex { get => currentTimelineIndex; set => Set(ref currentTimelineIndex, value); }

        private Color themeColor;
        public Color ThemeColor { get => themeColor; set => Set(ref themeColor, value); }

        private UserPermission permission = new();
        public UserPermission Permission { get => permission; set => Set(ref permission, value); }
    }
}
