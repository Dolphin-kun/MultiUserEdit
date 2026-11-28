using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiUserEdit.Networking
{
    internal static class RtcNative
    {
        private const string Library = "datachannel";

        public const int StateConnected = 2;
        public const int StateDisconnected = 3;
        public const int StateFailed = 4;
        public const int StateClosed = 5;

        [StructLayout(LayoutKind.Sequential)]
        public struct Configuration
        {
            public IntPtr IceServers;
            public int IceServersCount;
            public IntPtr ProxyServer;
            public IntPtr BindAddress;
            public int CertificateType;
            public int IceTransportPolicy;
            public byte EnableIceTcp;
            public byte EnableIceUdpMux;
            public byte DisableAutoNegotiation;
            public byte ForceMediaTransport;
            public ushort PortRangeBegin;
            public ushort PortRangeEnd;
            public int Mtu;
            public int MaxMessageSize;
        }

        public const int CodecH264 = 0;
        public const int DirectionSendOnly = 1;
        public const int NalSeparatorStartSequence = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct TrackInit
        {
            public int Direction;
            public int Codec;
            public int PayloadType;
            public uint Ssrc;
            public IntPtr Mid;
            public IntPtr Name;
            public IntPtr Msid;
            public IntPtr TrackId;
            public IntPtr Profile;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PacketizerInit
        {
            public uint Ssrc;
            public IntPtr Cname;
            public byte PayloadType;
            public uint ClockRate;
            public ushort SequenceNumber;
            public uint Timestamp;
            public ushort MaxFragmentSize;
            public int NalSeparator;
            public int ObuPacketization;
            public byte PlayoutDelayId;
            public ushort PlayoutDelayMin;
            public ushort PlayoutDelayMax;
            public byte ColorSpaceId;
            public byte ColorChromaSitingHorz;
            public byte ColorChromaSitingVert;
            public byte ColorRange;
            public byte ColorPrimaries;
            public byte ColorTransfer;
            public byte ColorMatrix;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void PliCallback(int track, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DescriptionCallback(int pc, IntPtr sdp, IntPtr type, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void CandidateCallback(int pc, IntPtr candidate, IntPtr mid, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void StateCallback(int pc, int state, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DataChannelCallback(int pc, int dc, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void SimpleCallback(int id, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ErrorCallback(int id, IntPtr error, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void MessageCallback(int id, IntPtr message, int size, IntPtr user);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void rtcPreload();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcCreatePeerConnection(ref Configuration config);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcDeletePeerConnection(int pc);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetLocalDescriptionCallback(int pc, DescriptionCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetLocalCandidateCallback(int pc, CandidateCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetStateChangeCallback(int pc, StateCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetDataChannelCallback(int pc, DataChannelCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetRemoteDescription(int pc, byte[] sdp, byte[] type);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcAddRemoteCandidate(int pc, byte[] candidate, byte[]? mid);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcCreateDataChannel(int pc, byte[] label);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcDeleteDataChannel(int dc);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcGetDataChannelLabel(int dc, byte[] buffer, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetOpenCallback(int id, SimpleCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetClosedCallback(int id, SimpleCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetErrorCallback(int id, ErrorCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetMessageCallback(int id, MessageCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern unsafe int rtcSendMessage(int id, byte* data, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcGetBufferedAmount(int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcIsOpen(int id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcAddTrackEx(int pc, ref TrackInit init);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcDeleteTrack(int track);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetH264Packetizer(int track, ref PacketizerInit init);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcChainRtcpSrReporter(int track);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcChainRtcpNackResponder(int track, uint maxStoredPackets);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcChainPliHandler(int track, PliCallback cb);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int rtcSetTrackRtpTimestamp(int track, uint timestamp);

        public static byte[] Utf8(string text)
        {
            var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
            Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
            return bytes;
        }

        public static string Text(IntPtr pointer) => pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;

        public static bool TryLoad(out string? error)
        {
            try
            {
                var directory = Path.GetDirectoryName(typeof(RtcNative).Assembly.Location) ?? AppContext.BaseDirectory;
                var path = Path.Combine(directory, Library + ".dll");
                if (!File.Exists(path))
                {
                    error = "missing " + path;
                    return false;
                }
                NativeLibrary.Load(path);
                rtcPreload();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
        }
    }

    internal sealed class RtcConnection : IDisposable
    {
        private static readonly ConcurrentDictionary<int, RtcConnection> connections = new();
        private static readonly RtcNative.DescriptionCallback OnDescription = (pc, sdp, type, _) => Find(pc)?.LocalDescription?.Invoke(RtcNative.Text(sdp), RtcNative.Text(type));
        private static readonly RtcNative.CandidateCallback OnCandidate = (pc, candidate, mid, _) => Find(pc)?.LocalCandidate?.Invoke(RtcNative.Text(candidate), RtcNative.Text(mid));
        private static readonly RtcNative.StateCallback OnState = (pc, state, _) => Find(pc)?.StateChanged?.Invoke(state);
        private static readonly RtcNative.DataChannelCallback OnChannel = (pc, dc, _) =>
        {
            var connection = Find(pc);
            if (connection == null) return;
            var channel = new RtcChannel(dc);
            connection.ChannelOpened?.Invoke(channel);
        };

        private readonly int id;
        private int disposed;

        public event Action<string, string>? LocalDescription;
        public event Action<string, string>? LocalCandidate;
        public event Action<int>? StateChanged;
        public event Action<RtcChannel>? ChannelOpened;

        private static RtcConnection? Find(int id) => connections.TryGetValue(id, out var connection) ? connection : null;

        public RtcConnection(IReadOnlyList<string> iceServers, int maxMessageSize)
        {
            var servers = iceServers.Select(server => Marshal.StringToCoTaskMemUTF8(server)).ToArray();
            var array = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, servers.Length));
            try
            {
                for (var i = 0; i < servers.Length; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, servers[i]);
                var config = new RtcNative.Configuration
                {
                    IceServers = servers.Length > 0 ? array : IntPtr.Zero,
                    IceServersCount = servers.Length,
                    MaxMessageSize = maxMessageSize,
                };
                id = RtcNative.rtcCreatePeerConnection(ref config);
                if (id < 0) throw new InvalidOperationException($"rtcCreatePeerConnection failed ({id})");
            }
            finally
            {
                foreach (var server in servers) Marshal.FreeCoTaskMem(server);
                Marshal.FreeHGlobal(array);
            }

            connections[id] = this;
            RtcNative.rtcSetLocalDescriptionCallback(id, OnDescription);
            RtcNative.rtcSetLocalCandidateCallback(id, OnCandidate);
            RtcNative.rtcSetStateChangeCallback(id, OnState);
            RtcNative.rtcSetDataChannelCallback(id, OnChannel);
        }

        public void SetRemoteDescription(string sdp, string type) => RtcNative.rtcSetRemoteDescription(id, RtcNative.Utf8(sdp), RtcNative.Utf8(type));

        public void AddRemoteCandidate(string candidate, string? mid) => RtcNative.rtcAddRemoteCandidate(id, RtcNative.Utf8(candidate), string.IsNullOrEmpty(mid) ? null : RtcNative.Utf8(mid));

        public RtcTrack? AddH264Track(string mid, uint ssrc, int payloadType)
        {
            var midText = Marshal.StringToCoTaskMemUTF8(mid);
            var name = Marshal.StringToCoTaskMemUTF8("preview");
            var msid = Marshal.StringToCoTaskMemUTF8("preview");
            var profile = Marshal.StringToCoTaskMemUTF8("profile-level-id=42e01f;packetization-mode=1;level-asymmetry-allowed=1");
            try
            {
                var init = new RtcNative.TrackInit
                {
                    Direction = RtcNative.DirectionSendOnly,
                    Codec = RtcNative.CodecH264,
                    PayloadType = payloadType,
                    Ssrc = ssrc,
                    Mid = midText,
                    Name = name,
                    Msid = msid,
                    TrackId = name,
                    Profile = profile,
                };
                var track = RtcNative.rtcAddTrackEx(id, ref init);
                return track < 0 ? null : new RtcTrack(track, ssrc, payloadType);
            }
            finally
            {
                Marshal.FreeCoTaskMem(midText);
                Marshal.FreeCoTaskMem(name);
                Marshal.FreeCoTaskMem(msid);
                Marshal.FreeCoTaskMem(profile);
            }
        }

        public RtcChannel? CreateChannel(string label)
        {
            var channel = RtcNative.rtcCreateDataChannel(id, RtcNative.Utf8(label));
            return channel < 0 ? null : new RtcChannel(channel, label);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) return;
            RtcNative.rtcDeletePeerConnection(id);
            connections.TryRemove(id, out _);
            LocalDescription = null;
            LocalCandidate = null;
            StateChanged = null;
            ChannelOpened = null;
        }
    }

    internal sealed class RtcChannel : IDisposable
    {
        private static readonly ConcurrentDictionary<int, RtcChannel> channels = new();
        private static readonly RtcNative.SimpleCallback OnOpen = (id, _) => Find(id)?.Opened();
        private static readonly RtcNative.SimpleCallback OnClosed = (id, _) => Find(id)?.Closed();
        private static readonly RtcNative.ErrorCallback OnError = (id, _, _) => Find(id)?.Failed();
        private static readonly RtcNative.MessageCallback OnMessage = (id, message, size, _) => Find(id)?.Received(message, size);

        private readonly int id;
        private int disposed;
        private volatile bool open;

        public string Label { get; }
        public bool IsOpen => open;
        public bool IsClosed { get; private set; }

        public event Action? Open;
        public event Action? Close;
        public event Action<string>? Text;
        public event Action<byte[]>? Binary;

        private static RtcChannel? Find(int id) => channels.TryGetValue(id, out var channel) ? channel : null;

        public RtcChannel(int id, string? label = null)
        {
            this.id = id;
            Label = label ?? ReadLabel(id);
            channels[id] = this;
            RtcNative.rtcSetOpenCallback(id, OnOpen);
            RtcNative.rtcSetClosedCallback(id, OnClosed);
            RtcNative.rtcSetErrorCallback(id, OnError);
            RtcNative.rtcSetMessageCallback(id, OnMessage);
            if (RtcNative.rtcIsOpen(id) > 0) open = true;
        }

        private static string ReadLabel(int id)
        {
            var buffer = new byte[256];
            var length = RtcNative.rtcGetDataChannelLabel(id, buffer, buffer.Length);
            return length > 0 ? Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length - 1)).TrimEnd('\0') : string.Empty;
        }

        private void Opened()
        {
            open = true;
            Open?.Invoke();
        }

        private void Closed()
        {
            open = false;
            IsClosed = true;
            Close?.Invoke();
        }

        private void Failed()
        {
            open = false;
            IsClosed = true;
            Close?.Invoke();
        }

        private void Received(IntPtr message, int size)
        {
            if (size >= 0)
            {
                var data = new byte[size];
                if (size > 0) Marshal.Copy(message, data, 0, size);
                Binary?.Invoke(data);
            }
            else
            {
                Text?.Invoke(RtcNative.Text(message));
            }
        }

        public int BufferedAmount => Math.Max(0, RtcNative.rtcGetBufferedAmount(id));

        public unsafe bool Send(ReadOnlySpan<byte> data)
        {
            fixed (byte* pointer = data) return RtcNative.rtcSendMessage(id, pointer, data.Length) >= 0;
        }

        public unsafe bool Send(string text)
        {
            var bytes = RtcNative.Utf8(text);
            fixed (byte* pointer = bytes) return RtcNative.rtcSendMessage(id, pointer, -1) >= 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) return;
            open = false;
            IsClosed = true;
            RtcNative.rtcDeleteDataChannel(id);
            channels.TryRemove(id, out _);
            Open = null;
            Close = null;
            Text = null;
            Binary = null;
        }
    }

    internal sealed class RtcTrack : IDisposable
    {
        private const uint ClockRate = 90000;
        private static readonly ConcurrentDictionary<int, RtcTrack> tracks = new();
        private static readonly RtcNative.SimpleCallback OnOpen = (id, _) => Find(id)?.Opened();
        private static readonly RtcNative.SimpleCallback OnClosed = (id, _) => Find(id)?.Closed();
        private static readonly RtcNative.PliCallback OnPli = (id, _) => Find(id)?.KeyframeRequested?.Invoke();

        private readonly int id;
        private readonly uint baseTimestamp;
        private int disposed;
        private volatile bool open;

        public bool IsOpen => open;

        public event Action? Open;
        public event Action? Close;
        public event Action? KeyframeRequested;

        private static RtcTrack? Find(int id) => tracks.TryGetValue(id, out var track) ? track : null;

        public RtcTrack(int id, uint ssrc, int payloadType)
        {
            this.id = id;
            baseTimestamp = (uint)Random.Shared.Next();
            tracks[id] = this;

            var cname = Marshal.StringToCoTaskMemUTF8("preview");
            try
            {
                var init = new RtcNative.PacketizerInit
                {
                    Ssrc = ssrc,
                    Cname = cname,
                    PayloadType = (byte)payloadType,
                    ClockRate = ClockRate,
                    SequenceNumber = (ushort)Random.Shared.Next(ushort.MaxValue),
                    Timestamp = baseTimestamp,
                    MaxFragmentSize = 1200,
                    NalSeparator = RtcNative.NalSeparatorStartSequence,
                };
                RtcNative.rtcSetH264Packetizer(id, ref init);
            }
            finally
            {
                Marshal.FreeCoTaskMem(cname);
            }
            RtcNative.rtcChainRtcpSrReporter(id);
            RtcNative.rtcChainRtcpNackResponder(id, 512);
            RtcNative.rtcChainPliHandler(id, OnPli);
            RtcNative.rtcSetOpenCallback(id, OnOpen);
            RtcNative.rtcSetClosedCallback(id, OnClosed);
            if (RtcNative.rtcIsOpen(id) > 0) open = true;
        }

        private void Opened()
        {
            open = true;
            Open?.Invoke();
        }

        private void Closed()
        {
            open = false;
            Close?.Invoke();
        }

        public unsafe bool SendFrame(ReadOnlySpan<byte> annexB, double seconds, out uint timestamp)
        {
            timestamp = unchecked(baseTimestamp + (uint)(seconds * ClockRate));
            if (!open || disposed != 0) return false;
            RtcNative.rtcSetTrackRtpTimestamp(id, timestamp);
            fixed (byte* pointer = annexB) return RtcNative.rtcSendMessage(id, pointer, annexB.Length) >= 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) return;
            open = false;
            tracks.TryRemove(id, out _);
            Open = null;
            Close = null;
            KeyframeRequested = null;
        }
    }
}
