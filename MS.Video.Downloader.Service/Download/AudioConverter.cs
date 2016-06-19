﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Xml;
using TagLib;
using File = TagLib.File;
using Tag = TagLib.Id3v2.Tag;

namespace ms.video.downloader.service.Download
{
    public class AudioConverter
    {
        private readonly EntryDownloadStatusEventHandler _onEntryDownloadStatusChange;
        private readonly YoutubeEntry _youtubeEntry;
        private readonly string _applicationPath;

        public AudioConverter(YoutubeEntry youtubeEntry, EntryDownloadStatusEventHandler onEntryDownloadStatusChange)
        {
            _youtubeEntry = youtubeEntry;
            _onEntryDownloadStatusChange = onEntryDownloadStatusChange;
            _applicationPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        }

        public void ConvertToMp3()
        {
            if (_youtubeEntry.DownloadState != DownloadState.DownloadFinish) return;
            var title = DownloadHelper.GetLegalPath(_youtubeEntry.Title);
            var audioFile = _youtubeEntry.DownloadFolder.CreateFile(title + ".mp3");
            var videoFile = _youtubeEntry.VideoFolder.CreateFile(title + _youtubeEntry.VideoExtension);
            if (!videoFile.Exists()) return;
            if (audioFile.Exists()) {
                if (_onEntryDownloadStatusChange != null) _onEntryDownloadStatusChange(_youtubeEntry, DownloadState.Ready, 100.0);
            } else {
                try {
                    TranscodeFile(videoFile, audioFile);
                } catch {
                    if (_onEntryDownloadStatusChange != null) _onEntryDownloadStatusChange(_youtubeEntry, DownloadState.Error, 100.0);
                }
            }
        }

        protected void TranscodeFile(StorageFile videoFile, StorageFile audioFile)
        {
            var tmpFile = KnownFolders.TempFolder.CreateFile(Guid.NewGuid().ToString("N") + ".mp3");
            var arguments = String.Format("-i \"{0}\" -acodec mp3 -y -ac 2 -ab 160 \"{1}\"", videoFile, tmpFile);
            var process = new Process {
                EnableRaisingEvents = true,
                StartInfo = {
                    FileName = _applicationPath + "\\Executables\\ffmpeg.exe",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                }
            };
            if (_onEntryDownloadStatusChange != null) _onEntryDownloadStatusChange(_youtubeEntry, DownloadState.ConvertAudioStart, 50);

            process.Start();
            using (var d = process.StandardError) {
                var duration = new TimeSpan();
                do {
                    var s = d.ReadLine() ?? "";
                    if (s.Contains("Duration: ")) {
                        duration = ParseDuration("Duration: ", ',', s);
                    }
                    else {
                        if (s.Contains(" time=")) {
                            var current = ParseDuration(" time=", ' ', s);
                            var percentage = (current.TotalMilliseconds / duration.TotalMilliseconds) * 50;
                            if (_onEntryDownloadStatusChange != null) _onEntryDownloadStatusChange(_youtubeEntry, DownloadState.DownloadProgressChanged, 50 + percentage );
                        }
                    }
                } while (!d.EndOfStream);
            }
            process.WaitForExit();
            DownloadState state;
            if (process.ExitCode == 0) {
                try {
                    Tag(tmpFile.ToString());
                    tmpFile.Move(audioFile);
                    state = DownloadState.Ready;
                } catch {
                    state = DownloadState.Error;
                }
            } else {
                if(tmpFile.Exists())
                    tmpFile.Delete();
                state = DownloadState.Error;
            }
            if (_onEntryDownloadStatusChange != null) _onEntryDownloadStatusChange(_youtubeEntry, state, 100.0);
            process.Close();
        }

        private TimeSpan ParseDuration(string start, char end, string s)
        {
            if (s == null) return new TimeSpan(0);
            var i = s.IndexOf(start, StringComparison.Ordinal);
            if (i < 0) return new TimeSpan(0);
            i += start.Length;
            var j = s.IndexOf(end, i);
            j = j - i;
            var timespan = s.Substring(i, j);
            var ts = TimeSpan.Parse(timespan);
            return ts;
        }

        #region Async Transcode

        protected void _TranscodeFile(StorageFile videoFile, StorageFile audioFile)
        {
            var arguments = String.Format("-i \"{0}\" -acodec mp3 -y -ac 2 -ab 160 \"{1}\"", videoFile, audioFile);
            var process = new Process {
                EnableRaisingEvents = true,
                StartInfo = {
                    FileName = _applicationPath + "\\Executables\\ffmpeg.exe",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                }
            };
            process.OutputDataReceived += (sender, args) => {
                var s = sender.ToString();
                var a = args.ToString();
                var state = (s + a == "Mert") ? DownloadState.Deleted : DownloadState.ConvertAudioStart;
                if (_onEntryDownloadStatusChange != null) _onEntryDownloadStatusChange(_youtubeEntry, state, 100.0);
            };
            process.ErrorDataReceived += (sender, args) => {
                var s = sender.ToString();
                var a = args.ToString();
                var state = (s + a == "Mert") ? DownloadState.Deleted : DownloadState.ConvertAudioStart;
                if (_onEntryDownloadStatusChange != null) _onEntryDownloadStatusChange(_youtubeEntry, state, 100.0);
            };
            process.Exited += (sender, args) => {
                DownloadState state;
                if (process.ExitCode == 0) {
                    try {
                        Tag(audioFile.ToString());
                        state = DownloadState.Ready;
                    }
                    catch {
                        state = DownloadState.Error;
                    }
                }
                else state = DownloadState.Error;
                if (_onEntryDownloadStatusChange != null) _onEntryDownloadStatusChange(_youtubeEntry, state, 100.0);
            };
            process.Start();
        }

        #endregion

        #region TagLib

        private void Tag(string filename)
        {
            var file = File.Create(filename);
            if (file == null) return;
            var tag = GetId3Tag();
            tag.CopyTo(file.Tag, true);
            file.Tag.Pictures = tag.Pictures;
            file.Save();
        }

        private Tag GetId3Tag()
        {
            var uri =
                new Uri(String.Format("https://gdata.youtube.com/feeds/api/videos/{0}?v=2", _youtubeEntry.YoutubeUrl.Id));
            var tag = new Tag { Title = _youtubeEntry.Title, Album = _youtubeEntry.ChannelName };
            try {
                var xml = new XmlDocument();
                var req = WebRequest.Create(uri);
                using (var resp = req.GetResponse()) {
                    using (var stream = resp.GetResponseStream()) {
                        if (stream != null) xml.Load(stream);
                    }
                }
                if (xml.DocumentElement != null) {
                    var manager = new XmlNamespaceManager(xml.NameTable);
                    manager.AddNamespace("root", "http://www.w3.org/2005/Atom");
                    manager.AddNamespace("app", "http://www.w3.org/2007/app");
                    manager.AddNamespace("media", "http://search.yahoo.com/mrss/");
                    manager.AddNamespace("gd", "http://schemas.google.com/g/2005");
                    manager.AddNamespace("yt", "http://gdata.youtube.com/schemas/2007");
                    tag.Title = GetText(xml, "media:group/media:title", manager);
                    tag.Lyrics = "MS.Video.Downloader\r\n" + GetText(xml, "media:group/media:description", manager);
                    tag.Copyright = GetText(xml, "media:group/media:license", manager);
                    tag.Album = _youtubeEntry.ChannelName;
                    tag.Composers = new[] {
                        "MS.Video.Downloader", "Youtube",
                        GetText(xml, "root:link[@rel=\"alternate\"]/@href", manager),
                        GetText(xml, "root:author/root:name", manager),
                        GetText(xml, "root:author/root:uri", manager)
                    };
                    var urlNodes = xml.DocumentElement.SelectNodes("media:group/media:thumbnail", manager);
                    var webClient = new WebClient();
                    var pics = new List<IPicture>();
                    if (urlNodes != null && urlNodes.Count > 0) {
                        foreach (XmlNode urlNode in urlNodes) {
                            var attributes = urlNode.Attributes;
                            if (attributes == null || attributes.Count <= 0) continue;
                            var url = attributes["url"];
                            if (url == null || String.IsNullOrEmpty(url.Value)) continue;
                            var data = webClient.DownloadData(url.Value);
                            IPicture pic = new Picture(new ByteVector(data));
                            pics.Add(pic);
                        }
                    }
                    tag.Pictures = pics.ToArray();
                }
            } catch { }
            return tag;
        }

        private static string GetText(XmlDocument xml, string xpath, XmlNamespaceManager manager)
        {
            if (xml.DocumentElement != null) {
                var node = xml.DocumentElement.SelectSingleNode(xpath, manager);
                return node == null ? "" : node.InnerText;
            }
            return "";
        }

        #endregion

    }
}