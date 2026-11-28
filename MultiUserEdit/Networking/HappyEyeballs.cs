using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace MultiUserEdit.Networking
{
    internal static class HappyEyeballs
    {
        private static readonly TimeSpan NextAttemptDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

        public static SocketsHttpHandler CreateHandler() => new() { ConnectCallback = ConnectAsync };

        public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
        {
            var addresses = Interleave(await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token));
            if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

            using var attemptsToken = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pending = new List<Task<Socket?>>();
            var errors = new List<Exception>();
            var next = 0;

            while (true)
            {
                if (next < addresses.Count)
                    pending.Add(AttemptAsync(addresses[next++], context.DnsEndPoint.Port, errors, attemptsToken.Token));

                if (pending.Count == 0) break;

                var waitForNext = next < addresses.Count
                    ? Task.Delay(NextAttemptDelay, attemptsToken.Token)
                    : Task.Delay(Timeout.Infinite, attemptsToken.Token);

                var finished = await Task.WhenAny(pending.Cast<Task>().Append(waitForNext));
                if (finished == waitForNext)
                {
                    token.ThrowIfCancellationRequested();
                    continue;
                }

                var attempt = (Task<Socket?>)finished;
                pending.Remove(attempt);

                var socket = await attempt;
                if (socket == null) continue;

                attemptsToken.Cancel();
                foreach (var loser in pending)
                    _ = loser.ContinueWith(task => task.Result?.Dispose(), TaskScheduler.Default);

                return new NetworkStream(socket, ownsSocket: true);
            }

            token.ThrowIfCancellationRequested();

            lock (errors)
            {
                throw errors.OfType<SocketException>().FirstOrDefault()
                    ?? errors.FirstOrDefault()
                    ?? new SocketException((int)SocketError.HostUnreachable);
            }
        }

        private static async Task<Socket?> AttemptAsync(IPAddress address, int port, List<Exception> errors, CancellationToken token)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(AttemptTimeout);

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
                if (token.IsCancellationRequested)
                {
                    socket.Dispose();
                    return null;
                }
                return socket;
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (!token.IsCancellationRequested)
                {
                    lock (errors) errors.Add(ex is OperationCanceledException ? new SocketException((int)SocketError.TimedOut) : ex);
                }
                return null;
            }
        }

        private static List<IPAddress> Interleave(IPAddress[] addresses)
        {
            if (addresses.Length == 0) return [];

            var preferred = addresses[0].AddressFamily;
            var first = addresses.Where(a => a.AddressFamily == preferred).ToList();
            var second = addresses.Where(a => a.AddressFamily != preferred).ToList();

            var ordered = new List<IPAddress>(addresses.Length);
            for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
            {
                if (i < first.Count) ordered.Add(first[i]);
                if (i < second.Count) ordered.Add(second[i]);
            }
            return ordered;
        }
    }
}
