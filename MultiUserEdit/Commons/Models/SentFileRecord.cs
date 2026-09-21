namespace MultiUserEdit.Commons.Models
{
    public sealed record SentFileRecord(string FullPath, long SizeBytes, DateTime SentAt, bool WasTransferred);
}
