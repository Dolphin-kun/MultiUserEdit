using System.IO;
using FFmpeg.AutoGen;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg.Utilities;

namespace MultiUserEdit.Commons
{
    internal sealed unsafe class H264Encoder : IDisposable
    {
        private static readonly string[] Candidates = ["h264_nvenc", "h264_amf", "h264_qsv", "h264_mf", "libopenh264"];
        private static readonly Lock gate = new();
        private static string? preferred;
        private static readonly HashSet<string> broken = [];

        private AVCodecContext* context;
        private AVFrame* frame;
        private AVFrame* source;
        private AVPacket* packet;
        private SwsContext* scaler;
        private int scalerWidth;
        private int scalerHeight;
        private long pts;

        public string Name { get; private set; } = string.Empty;
        public int Width { get; }
        public int Height { get; }

        private H264Encoder(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public static bool IsAvailable
        {
            get
            {
                try
                {
                    FFmpegModule.EnsureInitialized(FFmpegResourceLocator.GetFFmpegDllDirectory());
                    return FFmpegModule.IsAvailable();
                }
                catch
                {
                    return false;
                }
            }
        }

        public static H264Encoder? Create(int width, int height, int fps, long bitrate)
        {
            if (!IsAvailable) return null;
            width &= ~1;
            height &= ~1;
            if (width < 16 || height < 16) return null;

            string[] order;
            lock (gate)
            {
                order = preferred != null ? [preferred, .. Candidates.Where(c => c != preferred && !broken.Contains(c))] : [.. Candidates.Where(c => !broken.Contains(c))];
            }

            foreach (var name in order)
            {
                var encoder = new H264Encoder(width, height);
                if (encoder.TryOpen(name, fps, bitrate))
                {
                    lock (gate) preferred = name;
                    return encoder;
                }
                encoder.Dispose();
                lock (gate)
                {
                    broken.Add(name);
                    if (preferred == name) preferred = null;
                }
            }
            return null;
        }

        private bool TryOpen(string name, int fps, long bitrate)
        {
            try
            {
                var codec = ffmpeg.avcodec_find_encoder_by_name(name);
                if (codec == null) return false;

                context = ffmpeg.avcodec_alloc_context3(codec);
                if (context == null) return false;

                var pixelFormat = name == "libopenh264" ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_NV12;
                context->width = Width;
                context->height = Height;
                context->pix_fmt = pixelFormat;
                context->time_base = new AVRational { num = 1, den = Math.Max(1, fps) };
                context->framerate = new AVRational { num = Math.Max(1, fps), den = 1 };
                context->bit_rate = bitrate;
                context->rc_max_rate = bitrate * 3 / 2;
                context->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate);
                context->gop_size = fps * 60;
                context->max_b_frames = 0;
                context->thread_count = 1;
                context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
                context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
                context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
                context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;

                var options = context->priv_data;
                switch (name)
                {
                    case "h264_nvenc":
                        ffmpeg.av_opt_set(options, "preset", "p2", 0);
                        ffmpeg.av_opt_set(options, "tune", "ull", 0);
                        ffmpeg.av_opt_set(options, "profile", "baseline", 0);
                        ffmpeg.av_opt_set(options, "rc", "cbr", 0);
                        ffmpeg.av_opt_set(options, "zerolatency", "1", 0);
                        ffmpeg.av_opt_set(options, "forced-idr", "1", 0);
                        break;
                    case "h264_amf":
                        ffmpeg.av_opt_set(options, "usage", "ultralowlatency", 0);
                        ffmpeg.av_opt_set(options, "profile", "constrained_baseline", 0);
                        ffmpeg.av_opt_set(options, "quality", "speed", 0);
                        ffmpeg.av_opt_set(options, "rc", "cbr", 0);
                        break;
                    case "h264_qsv":
                        ffmpeg.av_opt_set(options, "preset", "veryfast", 0);
                        ffmpeg.av_opt_set(options, "profile", "baseline", 0);
                        ffmpeg.av_opt_set(options, "async_depth", "1", 0);
                        ffmpeg.av_opt_set(options, "look_ahead", "0", 0);
                        ffmpeg.av_opt_set(options, "forced_idr", "1", 0);
                        break;
                    case "h264_mf":
                        ffmpeg.av_opt_set(options, "scenario", "display_remoting", 0);
                        ffmpeg.av_opt_set(options, "rate_control", "cbr", 0);
                        ffmpeg.av_opt_set(options, "hw_encoding", "1", 0);
                        context->profile = ffmpeg.AV_PROFILE_H264_BASELINE;
                        break;
                    case "libopenh264":
                        ffmpeg.av_opt_set(options, "profile", "constrained_baseline", 0);
                        ffmpeg.av_opt_set(options, "allow_skip_frames", "0", 0);
                        ffmpeg.av_opt_set(options, "rc_mode", "bitrate", 0);
                        break;
                }

                if (ffmpeg.avcodec_open2(context, codec, null) < 0) return false;

                frame = ffmpeg.av_frame_alloc();
                frame->format = (int)pixelFormat;
                frame->width = Width;
                frame->height = Height;
                if (ffmpeg.av_frame_get_buffer(frame, 32) < 0) return false;

                source = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();
                Name = name;

                var probe = new byte[Width * Height * 4];
                fixed (byte* pixels = probe)
                {
                    var output = Encode(pixels, Width * 4, Width, Height, keyframe: true);
                    if (output == null || output.Length == 0) return false;
                }
                pts = 0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public byte[]? Encode(byte* bgra, int stride, int sourceWidth, int sourceHeight, bool keyframe)
        {
            if (context == null || frame == null) return null;
            if (scaler == null || scalerWidth != sourceWidth || scalerHeight != sourceHeight)
            {
                if (scaler != null) ffmpeg.sws_freeContext(scaler);
                scaler = ffmpeg.sws_getContext(sourceWidth, sourceHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                    Width, Height, (AVPixelFormat)frame->format, (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (scaler == null) return null;
                scalerWidth = sourceWidth;
                scalerHeight = sourceHeight;
            }

            if (ffmpeg.av_frame_make_writable(frame) < 0) return null;

            var sourceData = new byte_ptrArray4();
            sourceData[0] = bgra;
            var sourceStride = new int_array4();
            sourceStride[0] = stride;
            var targetData = new byte_ptrArray4();
            var targetStride = new int_array4();
            for (uint i = 0; i < 4; i++)
            {
                targetData[i] = frame->data[i];
                targetStride[i] = frame->linesize[i];
            }
            ffmpeg.sws_scale(scaler, sourceData.ToArray(), sourceStride.ToArray(), 0, sourceHeight, targetData.ToArray(), targetStride.ToArray());

            frame->pts = pts++;
            frame->pict_type = keyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
            if (keyframe) frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            else frame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;

            if (ffmpeg.avcodec_send_frame(context, frame) < 0) return null;

            using var output = new MemoryStream();
            while (true)
            {
                var result = ffmpeg.avcodec_receive_packet(context, packet);
                if (result < 0) break;
                output.Write(new ReadOnlySpan<byte>(packet->data, packet->size));
                ffmpeg.av_packet_unref(packet);
            }
            return output.ToArray();
        }

        public void Dispose()
        {
            if (scaler != null)
            {
                ffmpeg.sws_freeContext(scaler);
                scaler = null;
            }
            if (packet != null)
            {
                var p = packet;
                ffmpeg.av_packet_free(&p);
                packet = null;
            }
            if (source != null)
            {
                var s = source;
                ffmpeg.av_frame_free(&s);
                source = null;
            }
            if (frame != null)
            {
                var f = frame;
                ffmpeg.av_frame_free(&f);
                frame = null;
            }
            if (context != null)
            {
                var c = context;
                ffmpeg.avcodec_free_context(&c);
                context = null;
            }
        }
    }
}
