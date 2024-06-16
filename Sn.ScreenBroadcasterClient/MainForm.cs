using System.Collections.Concurrent;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibCommon;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;

namespace Sn.ScreenBroadcasterClient
{
    public partial class MainForm : Form
    {
        TcpClient _client = new();
        Frame frame = new Frame();

        ConcurrentQueue<FramePackets> framePacketBytes = new();
        FramePackets? lastKeyFrame = default;

        CodecContext _codecContext = new(FFmpegUtilities.FindBestDecoder(AVCodecID.H264))
        {
            Width = 2560,
            Height = 1440,
            Framerate = new AVRational(1, 30),
            TimeBase = new AVRational(1, 30),
            PixelFormat = AVPixelFormat.Yuv420p,
            BitRate = 8000000
        };

        public MainForm()
        {
            InitializeComponent();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            Task.Run(async () =>
            {
                _client.Connect(new IPEndPoint(IPAddress.Loopback, 7777));

                var networkStream = _client.GetStream();
                var framePacketCountBytes = new byte[4];
                var frameIsKeyFrameBytes = new byte[4];
                var packetSizeBytes = new byte[4];

                while (true)
                {
                    await networkStream.ReadBlockAsync(framePacketCountBytes, 0, 4);
                    await networkStream.ReadBlockAsync(frameIsKeyFrameBytes, 0, 4);

                    var framePacketCount = BitConverter.ToInt32(framePacketCountBytes);
                    var frameIsKeyFrame = BitConverter.ToInt32(frameIsKeyFrameBytes);

                    List<byte[]> framePacketBytes = new();
                    for (int i = 0; i < framePacketCount; i++)
                    {
                        await networkStream.ReadBlockAsync(packetSizeBytes, 0, 4);
                        var packetSize = BitConverter.ToInt32(packetSizeBytes);
                        var body = new byte[packetSize];

                        await networkStream.ReadBlockAsync(body, 0, packetSize);
                        framePacketBytes.Add(body);
                    }

                    var currentFramePackets = new FramePackets(frameIsKeyFrame != 0, framePacketBytes);
                    this.framePacketBytes.Enqueue(currentFramePackets);

                    if (currentFramePackets.IsKeyFrame)
                    {
                        lastKeyFrame = currentFramePackets;
                    }
                }
            });

            Task.Run(() =>
            {
                _codecContext.Open(_codecContext.Codec);

                var isDecoder = _codecContext.Codec.IsDecoder;
                var graphics = paintControl.CreateGraphics();

                var bitmap = default(Bitmap);
                var videoFrameConverter = new VideoFrameConverter();

                while (true)
                {
                    while (this.framePacketBytes.Count > 5 &&
                        this.framePacketBytes.Where(frame => frame.IsKeyFrame).Count() > 1)
                    {
                        if (!this.framePacketBytes.TryPeek(out var peeked))
                            continue;
                        if (peeked == lastKeyFrame)
                            break;

                        this.framePacketBytes.TryDequeue(out _);
                        Console.WriteLine("Drop frame");
                    }

                    if (this.framePacketBytes.TryDequeue(out var framePacketBytes))
                    {
                        if (framePacketBytes == lastKeyFrame)
                        {
                            lastKeyFrame = null;
                        }

                        foreach (var packetBytes in framePacketBytes.PacketsBytes)
                        {
                            unsafe
                            {
                                fixed (byte* bodyPtr = packetBytes)
                                {
                                    AVPacket avPacket = default;
                                    ffmpeg.av_packet_from_data(&avPacket, bodyPtr, packetBytes.Length);

                                    using Packet packet = Packet.FromNative(&avPacket, false);

                                    _codecContext.SendPacket(packet);
                                }
                            }

                            CodecResult result;

                            do
                            {
                                result = _codecContext.ReceiveFrame(frame);

                                if (result == CodecResult.Success)
                                {
                                    using var convertedFrame = new Frame()
                                    {
                                        Width = frame.Width,
                                        Height = frame.Height,
                                        Format = (int)AVPixelFormat.Bgra,
                                    };

                                    convertedFrame.EnsureBuffer();
                                    convertedFrame.MakeWritable();
                                    videoFrameConverter.ConvertFrame(frame, convertedFrame);
                                    if (bitmap == null ||
                                        bitmap.Width != frame.Width ||
                                        bitmap.Height != frame.Height)
                                    {
                                        if (bitmap is not null)
                                        {
                                            bitmap.Dispose();
                                        }

                                        bitmap = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                                    }
                                    var bmpData = bitmap.LockBits(new Rectangle(default(Point), bitmap.Size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);

                                    for (int y = 0; y < bitmap.Height; y++)
                                    {
                                        unsafe
                                        {
                                            NativeMemory.Copy((void*)(convertedFrame.Data[0] + convertedFrame.Linesize[0] * y), (void*)(bmpData.Scan0 + bmpData.Stride * y), (nuint)bmpData.Stride);
                                        }
                                    }

                                    bitmap.UnlockBits(bmpData);

                                    Invoke(() =>
                                    {
                                        graphics.DrawImage(bitmap, default(Point));
                                    });
                                }
                            }
                            while (result == CodecResult.Success);
                        }
                    }
                }
            });
        }
    }
}
