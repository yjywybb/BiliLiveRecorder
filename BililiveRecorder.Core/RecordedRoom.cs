﻿using BililiveRecorder.Core.Config;
using BililiveRecorder.FlvProcessor;
using JetBrains.Annotations;
using NLog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace BililiveRecorder.Core
{
    public class RecordedRoom : IRecordedRoom
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        private static readonly Random random = new Random();

        private int _roomid;
        private int _realRoomid;
        private string _streamerName;
        private string _title;

        public int ShortRoomId
        {
            get => _roomid;
            private set
            {
                if (value == _roomid) { return; }
                _roomid = value;
                TriggerPropertyChanged(nameof(ShortRoomId));
            }
        }
        public int RoomId
        {
            get => _realRoomid;
            private set
            {
                if (value == _realRoomid) { return; }
                _realRoomid = value;
                TriggerPropertyChanged(nameof(RoomId));
            }
        }
        public string StreamerName
        {
            get => _streamerName;
            private set
            {
                if (value == _streamerName) { return; }
                _streamerName = value;
                TriggerPropertyChanged(nameof(StreamerName));
            }
        }

        public string Title
        {
            get => _title;
            private set
            {
                if (value == _title) { return; }
                _title = value;
                TriggerPropertyChanged(nameof(Title));
            }
        }

        public bool IsMonitoring => StreamMonitor.IsMonitoring;
        public bool IsRecording => !(StreamDownloadTask?.IsCompleted ?? true);

        private readonly Func<IFlvStreamProcessor> newIFlvStreamProcessor;
        private IFlvStreamProcessor _processor;
        public IFlvStreamProcessor Processor
        {
            get => _processor;
            private set
            {
                if (value == _processor) { return; }
                _processor = value;
                TriggerPropertyChanged(nameof(Processor));
            }
        }

        private ConfigV1 _config { get; }
        public IStreamMonitor StreamMonitor { get; }

        private bool _retry = true;
        private HttpResponseMessage _response;
        private Stream _stream;
        private Task StartupTask = null;
        private readonly object StartupTaskLock = new object();
        public Task StreamDownloadTask = null;
        public CancellationTokenSource cancellationTokenSource = null;

        private double _DownloadSpeedPersentage = 0;
        private double _DownloadSpeedMegaBitps = 0;
        private long _lastUpdateSize = 0;
        private int _lastUpdateTimestamp = 0;
        public DateTime LastUpdateDateTime { get; private set; } = DateTime.Now;
        public double DownloadSpeedPersentage
        {
            get { return _DownloadSpeedPersentage; }
            private set { if (value != _DownloadSpeedPersentage) { _DownloadSpeedPersentage = value; TriggerPropertyChanged(nameof(DownloadSpeedPersentage)); } }
        }
        public double DownloadSpeedMegaBitps
        {
            get { return _DownloadSpeedMegaBitps; }
            private set { if (value != _DownloadSpeedMegaBitps) { _DownloadSpeedMegaBitps = value; TriggerPropertyChanged(nameof(DownloadSpeedMegaBitps)); } }
        }

        public RecordedRoom(ConfigV1 config,
            Func<int, IStreamMonitor> newIStreamMonitor,
            Func<IFlvStreamProcessor> newIFlvStreamProcessor,
            int roomid)
        {
            this.newIFlvStreamProcessor = newIFlvStreamProcessor;

            _config = config;

            RoomId = roomid;
            StreamerName = "...";

            StreamMonitor = newIStreamMonitor(RoomId);
            StreamMonitor.RoomInfoUpdated += StreamMonitor_RoomInfoUpdated;
            StreamMonitor.StreamStarted += StreamMonitor_StreamStarted;
            StreamMonitor.FetchRoomInfoAsync();
        }

        private void StreamMonitor_RoomInfoUpdated(object sender, RoomInfoUpdatedArgs e)
        {
            RoomId = e.RoomInfo.RoomId;
            ShortRoomId = e.RoomInfo.ShortRoomId;
            StreamerName = e.RoomInfo.UserName;
            StreamMonitor.StreamerName = StreamerName;
            Title = e.RoomInfo.Title;
            StreamMonitor.Title = Title;
        }

        public bool Start()
        {
            if (disposedValue)
            {
                throw new ObjectDisposedException(nameof(RecordedRoom));
            }

            var r = StreamMonitor.Start();
            TriggerPropertyChanged(nameof(IsMonitoring));
            return r;
        }

        public void Stop()
        {
            if (disposedValue)
            {
                throw new ObjectDisposedException(nameof(RecordedRoom));
            }

            StreamMonitor.Stop();
            StreamMonitor.IsRecording = false;
            TriggerPropertyChanged(nameof(IsMonitoring));
        }

        public void RefreshRoomInfo()
        {
            if (disposedValue)
            {
                throw new ObjectDisposedException(nameof(RecordedRoom));
            }

            StreamMonitor.FetchRoomInfoAsync();
        }

        private void StreamMonitor_StreamStarted(object sender, StreamStartedArgs e)
        {
            lock (StartupTaskLock)
            {
                if (!IsRecording && (StartupTask?.IsCompleted ?? true))
                {
                    StartupTask = _StartRecordAsync();
                }
            }
        }

        public void StartRecord()
        {
            if (disposedValue)
            {
                throw new ObjectDisposedException(nameof(RecordedRoom));
            }

            StreamMonitor.Check(TriggerType.Manual);
        }

        public void StopRecord()
        {
            if (disposedValue)
            {
                throw new ObjectDisposedException(nameof(RecordedRoom));
            }

            _retry = false;
            try
            {
                if (cancellationTokenSource != null)
                {
                    cancellationTokenSource.Cancel();
                    if (!(StreamDownloadTask?.Wait(TimeSpan.FromSeconds(2)) ?? true))
                    {
                        logger.Log(RoomId, LogLevel.Warn, "停止录制超时，尝试强制关闭连接，请检查网络连接是否稳定");

                        _stream?.Close();
                        _stream?.Dispose();
                        _response?.Dispose();
                        StreamDownloadTask?.Wait();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log(RoomId, LogLevel.Error, "在尝试停止录制时发生错误，请检查网络连接是否稳定", ex);
            }
            finally
            {
                _retry = true;
            }
        }

        private async Task _StartRecordAsync()
        {
            if (IsRecording)
            {
                logger.Log(RoomId, LogLevel.Debug, "已经在录制中了");
                return;
            }

            // HttpWebRequest request = null;

            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;
            try
            {
                string flv_path = await BililiveAPI.GetPlayUrlAsync(RoomId);

            unwrap_redir:

                using (var client = new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                }))
                {

                    client.Timeout = TimeSpan.FromMilliseconds(_config.TimingStreamConnect);
                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                    client.DefaultRequestHeaders.UserAgent.Clear();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(Utils.UserAgent);
                    client.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com");
                    client.DefaultRequestHeaders.Add("Origin", "https://live.bilibili.com");


                    logger.Log(RoomId, LogLevel.Info, "连接直播服务器 " + new Uri(flv_path).Host);
                    logger.Log(RoomId, LogLevel.Debug, "直播流地址: " + flv_path);

                    _response = await client.GetAsync(flv_path, HttpCompletionOption.ResponseHeadersRead);
                }

                if (_response.StatusCode == HttpStatusCode.Redirect || _response.StatusCode == HttpStatusCode.Moved)
                {
                    // workaround for missing Referrer
                    flv_path = _response.Headers.Location.OriginalString;
                    _response.Dispose();
                    goto unwrap_redir;
                }
                else if (_response.StatusCode != HttpStatusCode.OK)
                {
                    logger.Log(RoomId, LogLevel.Info, string.Format("尝试下载直播流时服务器返回了 ({0}){1}", _response.StatusCode, _response.ReasonPhrase));

                    StreamMonitor.Check(TriggerType.HttpApiRecheck, (int)_config.TimingStreamRetry);

                    _CleanupFlvRequest();
                    return;
                }
                else
                {
                    Processor = newIFlvStreamProcessor().Initialize(GetStreamFilePath, GetClipFilePath, _config.EnabledFeature, _config.CuttingMode);
                    Processor.ClipLengthFuture = _config.ClipLengthFuture;
                    Processor.ClipLengthPast = _config.ClipLengthPast;
                    Processor.CuttingNumber = _config.CuttingNumber;
                    Processor.OnMetaData += (sender, e) =>
                    {
                        e.Metadata["BililiveRecorder"] = new Dictionary<string, object>()
                        {
                            {
                                "starttime",
                                DateTime.UtcNow
                            },
                            {
                                "version",
                                BuildInfo.Version + " " + BuildInfo.HeadShaShort
                            },
                            {
                                "roomid",
                                RoomId.ToString()
                            },
                            {
                                "streamername",
                                StreamerName
                            },
                        };
                    };
                    StreamMonitor.Time = DateTime.Now;
                    _stream = await _response.Content.ReadAsStreamAsync();

                    if (!new object[] { null, true }.Contains(_response.Headers.ConnectionClose))
                        _stream.ReadTimeout = 3 * 1000;

                    StreamDownloadTask = Task.Run(_ReadStreamLoop);
                    TriggerPropertyChanged(nameof(IsRecording));
                    if (_config.EnabledFeature != EnabledFeature.ClipOnly)
                    {
                        while (StreamMonitor.path == null || StreamMonitor.path != Processor.path)
                        {
                            Thread.Sleep(100);
                            try
                            {
                                StreamMonitor.path = Processor.path;
                            }
                            catch { }
                        }
                        StreamMonitor.IsRecording = true;
                    }
                    else
                    {
                        StreamMonitor.IsRecording = false;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // client.GetAsync timed out
                // useless exception message :/
                StreamMonitor.IsRecording = false;
                _CleanupFlvRequest();
                logger.Log(RoomId, LogLevel.Warn, "连接直播服务器超时。");
                StreamMonitor.Check(TriggerType.HttpApiRecheck, (int)_config.TimingStreamRetry);
            }
            catch (Exception ex)
            {
                StreamMonitor.IsRecording = false;
                _CleanupFlvRequest();
                logger.Log(RoomId, LogLevel.Warn, "启动直播流下载出错。" + (_retry ? "将重试启动。" : ""), ex);
                if (_retry)
                {
                    StreamMonitor.Check(TriggerType.HttpApiRecheck, (int)_config.TimingStreamRetry);
                }
            }
            return;
            async Task _ReadStreamLoop()
            {
                try
                {
                    const int BUF_SIZE = 1024 * 8;
                    byte[] buffer = new byte[BUF_SIZE];
                    while (!token.IsCancellationRequested)
                    {
                        int bytesRead = await _stream.ReadAsync(buffer, 0, BUF_SIZE, token);
                        _UpdateDownloadSpeed(bytesRead);
                        if (bytesRead != 0)
                        {
                            if (bytesRead != BUF_SIZE)
                            {
                                Processor.AddBytes(buffer.Take(bytesRead).ToArray());
                            }
                            else
                            {
                                Processor.AddBytes(buffer);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    StreamMonitor.IsRecording = false;
                    logger.Log(RoomId, LogLevel.Info,
                        (token.IsCancellationRequested ? "本地操作结束当前录制。" : "服务器关闭直播流，可能是直播已结束。")
                        + (_retry ? "将重试启动。" : ""));
                    if (_retry)
                    {
                        StreamMonitor.Check(TriggerType.HttpApiRecheck, (int)_config.TimingStreamRetry);
                    }
                }
                catch (Exception e)
                {
                    StreamMonitor.IsRecording = false;
                    if (e is ObjectDisposedException && token.IsCancellationRequested) { return; }

                    logger.Log(RoomId, LogLevel.Warn, "录播发生错误", e);
                }
                finally
                {
                    _CleanupFlvRequest();
                }
            }
            void _CleanupFlvRequest()
            {
                if (Processor != null)
                {
                    Processor.FinallizeFile();
                    Processor.Dispose();
                    Processor = null;
                }
                _stream?.Dispose();
                _stream = null;
                _response?.Dispose();
                _response = null;

                _lastUpdateTimestamp = 0;
                DownloadSpeedMegaBitps = 0d;
                DownloadSpeedPersentage = 0d;
                StreamMonitor.IsRecording = false;
                TriggerPropertyChanged(nameof(IsRecording));
            }
            void _UpdateDownloadSpeed(int bytesRead)
            {
                DateTime now = DateTime.Now;
                double passedSeconds = (now - LastUpdateDateTime).TotalSeconds;
                _lastUpdateSize += bytesRead;
                if (passedSeconds > 1.5)
                {
                    DownloadSpeedMegaBitps = _lastUpdateSize / passedSeconds * 8d / 1_000_000d; // mega bit per second
                    DownloadSpeedPersentage = (DownloadSpeedPersentage / 2) + ((Processor.TotalMaxTimestamp - _lastUpdateTimestamp) / passedSeconds / 1000 / 2); // ((RecordedTime/1000) / RealTime)%
                    _lastUpdateTimestamp = Processor.TotalMaxTimestamp;
                    _lastUpdateSize = 0;
                    LastUpdateDateTime = now;
                }
            }
        }

        // Called by API or GUI
        public void Clip()
        {
            Processor?.Clip();
        }

        public void Shutdown()
        {
            Dispose(true);
        }

        private string GetStreamFilePath() => FormatFilename(_config.RecordFilenameFormat);

        private string GetClipFilePath() => FormatFilename(_config.ClipFilenameFormat);

        private string FormatFilename(string formatString)
        {
            DateTime now = DateTime.Now;
            string date = now.ToString("yyyyMMdd");
            string time = now.ToString("HHmmss");
            string randomStr = random.Next(100, 999).ToString();

            var filename = formatString
                .Replace(@"{date}", date)
                .Replace(@"{time}", time)
                .Replace(@"{random}", randomStr)
                .Replace(@"{roomid}", RoomId.ToString())
                .Replace(@"{title}", Title.RemoveInvalidFileName())
                .Replace(@"{name}", StreamerName.RemoveInvalidFileName());

            if (!filename.EndsWith(".flv", StringComparison.OrdinalIgnoreCase))
                filename += ".flv";

            filename = filename.RemoveInvalidFileName(ignore_slash: true);
            filename = Path.Combine(_config.WorkDirectory, filename);
            filename = Path.GetFullPath(filename);

            if (!CheckPath(_config.WorkDirectory, Path.GetDirectoryName(filename)))
            {
                logger.Log(RoomId, LogLevel.Warn, "录制文件位置超出允许范围，请检查设置。将写入到默认路径。");
                filename = Path.Combine(_config.WorkDirectory, RoomId.ToString(), $"{RoomId}-{date}-{time}-{randomStr}.flv");
            }

            if (new FileInfo(filename).Exists)
            {
                logger.Log(RoomId, LogLevel.Warn, "录制文件名冲突，请检查设置。将写入到默认路径。");
                filename = Path.Combine(_config.WorkDirectory, RoomId.ToString(), $"{RoomId}-{date}-{time}-{randomStr}.flv");
            }

            return filename;
        }

        private static bool CheckPath(string parent, string child)
        {
            DirectoryInfo di_p = new DirectoryInfo(parent);
            DirectoryInfo di_c = new DirectoryInfo(child);

            if (di_c.FullName == di_p.FullName)
                return true;

            bool isParent = false;
            while (di_c.Parent != null)
            {
                if (di_c.Parent.FullName == di_p.FullName)
                {
                    isParent = true;
                    break;
                }
                else
                    di_c = di_c.Parent;
            }
            return isParent;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void TriggerPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        #region IDisposable Support
        private bool disposedValue = false; // 要检测冗余调用

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Stop();
                    StopRecord();
                    Processor?.Dispose();
                    StreamMonitor?.Dispose();
                    _response?.Dispose();
                    _stream?.Dispose();
                    cancellationTokenSource?.Dispose();
                }

                Processor = null;
                _response = null;
                _stream = null;
                cancellationTokenSource = null;

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // 请勿更改此代码。将清理代码放入以上 Dispose(bool disposing) 中。
            Dispose(true);
        }
        #endregion
    }
}
