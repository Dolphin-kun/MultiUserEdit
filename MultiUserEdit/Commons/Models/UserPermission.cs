namespace MultiUserEdit.Commons.Models
{
    public class UserPermission
    {
        public PermissionLevel Level { get; set; } = PermissionLevel.Full;

        public bool CanAddItems { get; set; } = true;
        public bool CanMoveItems { get; set; } = true;
        public bool CanDeleteItems { get; set; } = true;
        public bool CanDeleteOthersItems { get; set; } = true;
        public bool CanEditProperties { get; set; } = true;
        public bool CanAddScenes { get; set; } = true;
        public bool CanDeleteScenes { get; set; } = true;
        public bool CanShareFiles { get; set; } = true;
        public bool CanSyncSeekPosition { get; set; } = false;

        public static UserPermission CreateFromLevel(PermissionLevel level)
        {
            return level switch
            {
                PermissionLevel.Full => new UserPermission
                {
                    Level = PermissionLevel.Full,
                    CanAddItems = true,
                    CanMoveItems = true,
                    CanDeleteItems = true,
                    CanDeleteOthersItems = true,
                    CanEditProperties = true,
                    CanAddScenes = true,
                    CanDeleteScenes = true,
                    CanShareFiles = true,
                    CanSyncSeekPosition = false
                },
                PermissionLevel.Standard => new UserPermission
                {
                    Level = PermissionLevel.Standard,
                    CanAddItems = true,
                    CanMoveItems = true,
                    CanDeleteItems = true,
                    CanDeleteOthersItems = true,
                    CanEditProperties = true,
                    CanAddScenes = false,
                    CanDeleteScenes = false,
                    CanShareFiles = true,
                    CanSyncSeekPosition = false
                },
                PermissionLevel.ReadOnly => new UserPermission
                {
                    Level = PermissionLevel.ReadOnly,
                    CanAddItems = false,
                    CanMoveItems = false,
                    CanDeleteItems = false,
                    CanDeleteOthersItems = false,
                    CanEditProperties = false,
                    CanAddScenes = false,
                    CanDeleteScenes = false,
                    CanShareFiles = false,
                    CanSyncSeekPosition = false
                },
                PermissionLevel.LiveMirror => new UserPermission
                {
                    Level = PermissionLevel.LiveMirror,
                    CanAddItems = false,
                    CanMoveItems = false,
                    CanDeleteItems = false,
                    CanDeleteOthersItems = false,
                    CanEditProperties = false,
                    CanAddScenes = false,
                    CanDeleteScenes = false,
                    CanShareFiles = false,
                    CanSyncSeekPosition = true
                },
                _ => new UserPermission()
            };
        }
    }
}
