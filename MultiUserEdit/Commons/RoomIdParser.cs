using System.Web;

namespace MultiUserEdit.Commons
{
    internal static class RoomIdParser
    {
        public static string? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();

            if (AsRoomId(text) is { } direct) return direct;

            if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                var query = HttpUtility.ParseQueryString(uri.Query);
                return AsRoomId(query["roomId"])
                    ?? AsRoomId(query["id"])
                    ?? AsRoomId(uri.AbsolutePath.Trim('/').Split('/').LastOrDefault());
            }

            return text.StartsWith('/') ? AsRoomId(text.Trim('/')) : null;
        }

        private static string? AsRoomId(string? value) =>
            Guid.TryParse(value, out var id) ? id.ToString() : null;
    }
}
