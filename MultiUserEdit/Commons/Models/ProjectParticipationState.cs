namespace MultiUserEdit.Commons.Models
{
    // YMM4のプロジェクトファイル（Project.ToolStates）へ保存される、プロジェクト単位の参加履歴。
    // プラグイン設定（SettingsBase）ではなくプロジェクトに紐づけて保存することで、
    // 不要になったプロジェクトのデータが設定ファイルに残り続けるのを避けている。
    public class ProjectParticipationState
    {
        public Dictionary<string, ParticipationRecord> Participants { get; set; } = [];
    }

    public class ParticipationRecord
    {
        public string UserName { get; set; } = string.Empty;
        public double TotalSeconds { get; set; }
    }
}
