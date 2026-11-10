using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MultiUserEdit.Commons
{
    internal static class ErrorNotifier
    {
        private const int MaxListed = 15;

        private sealed class ListBlock(string lead, string note)
        {
            public string Lead { get; } = lead;
            public string Note { get; } = note;
            public List<string> Items { get; } = [];
        }

        private sealed class Notice(string title)
        {
            public string Title { get; } = title;
            public List<object> Blocks { get; } = [];
        }

        private static readonly ConcurrentDictionary<string, byte> notified = new();
        private static readonly Lock gate = new();
        private static readonly List<Notice> queue = [];
        private static bool flushScheduled;
        private static bool showing;

        public static void NotifyOnce(string title, string message)
        {
            Debug.WriteLine($"[MultiUserEdit] {title}: {message}");

            if (!notified.TryAdd($"{title}\n{message}", 0)) return;

            lock (gate)
            {
                var notice = NoticeFor(title);
                if (!notice.Blocks.OfType<string>().Contains(message)) notice.Blocks.Add(message);
            }

            ScheduleFlush();
        }

        public static void NotifyList(string title, string lead, string item, string note = "", bool once = true)
        {
            Debug.WriteLine($"[MultiUserEdit] {title}: {lead} {item}");

            if (once && !notified.TryAdd($"{title}\n{lead}\n{note}\n{item}", 0)) return;

            lock (gate)
            {
                var notice = NoticeFor(title);
                var block = notice.Blocks.OfType<ListBlock>().FirstOrDefault(b => b.Lead == lead && b.Note == note);
                if (block == null)
                {
                    block = new ListBlock(lead, note);
                    notice.Blocks.Add(block);
                }

                if (!block.Items.Contains(item)) block.Items.Add(item);
            }

            ScheduleFlush();
        }

        public static string Quote(string name) => $"「{name}」";

        private static Notice NoticeFor(string title)
        {
            var notice = queue.FirstOrDefault(n => n.Title == title);
            if (notice != null) return notice;

            notice = new Notice(title);
            queue.Add(notice);
            return notice;
        }

        private static void ScheduleFlush()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            lock (gate)
            {
                if (flushScheduled || showing) return;
                flushScheduled = true;
            }

            dispatcher.InvokeAsync(Flush, DispatcherPriority.Background);
        }

        private static void Flush()
        {
            while (true)
            {
                Notice[] batch;
                lock (gate)
                {
                    flushScheduled = false;
                    if (showing || queue.Count == 0) return;

                    batch = [.. queue];
                    queue.Clear();
                    showing = true;
                }

                try
                {
                    foreach (var notice in batch)
                    {
                        MessageBox.Show(Compose(notice), notice.Title, MessageBoxButton.OK);
                    }
                }
                finally
                {
                    lock (gate) showing = false;
                }
            }
        }

        private static string Compose(Notice notice)
        {
            var sections = new List<string>();

            foreach (var block in notice.Blocks)
            {
                if (block is string message)
                {
                    sections.Add(message);
                    continue;
                }

                if (block is not ListBlock list) continue;

                var builder = new StringBuilder();
                if (!string.IsNullOrEmpty(list.Lead)) builder.Append(list.Lead).Append("\n\n");

                builder.Append(string.Join("\n", list.Items.Take(MaxListed)));
                if (list.Items.Count > MaxListed) builder.Append($"\n（ほか {list.Items.Count - MaxListed} 件）");

                if (!string.IsNullOrEmpty(list.Note)) builder.Append("\n\n").Append(list.Note);

                sections.Add(builder.ToString());
            }

            return string.Join("\n\n", sections);
        }
    }
}
