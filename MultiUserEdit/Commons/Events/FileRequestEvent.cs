namespace MultiUserEdit.Commons.Events
{
    /// <summary>
    /// <see cref="FileAvailableEvent"/> への返答。告知した本人だけに宛てて送る。
    /// 既に持っている場合も <see cref="NeedsTransfer"/> = false で必ず返す。
    /// そうしないと送信側が「まだ返事をしていない人がいるのか、全員が持っているのか」を
    /// 区別できず、毎回タイムアウトまで待つことになってしまう。
    /// </summary>
    public record FileRequestEvent(
        Guid TransferId,
        Guid RequesterId,
        bool NeedsTransfer = true
    ) : EditEvent;
}
