namespace MultiUserEdit.Commons.Events
{
    /// <summary>
    /// これから送ろうとしているファイルの告知。実データは送らず、内容ハッシュだけを配る。
    /// 受信側は同じ内容のファイルを既に持っていれば何も要求せず、ローカルのファイルをそのまま使う。
    /// </summary>
    public record FileAvailableEvent(
        Guid TransferId,
        string FileName,
        long FileSize,
        string Hash
    ) : EditEvent;
}
