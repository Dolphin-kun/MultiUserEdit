namespace MultiUserEdit.Commons.Events
{
    public record FileTransferStartEvent(
        Guid TransferId,
        string FileName,
        long FileSize,
        int TotalChunks,
        // 受信側が書き込み位置を計算するために使う。送信側の設定が変わっても追従できるよう明示的に送る。
        int ChunkSize = 0
    ) : EditEvent;
}
