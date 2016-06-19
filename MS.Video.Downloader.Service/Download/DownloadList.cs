﻿using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ms.video.downloader.service.Download
{
    public delegate void ListDownloadStatusEventHandler(Feed list, Feed feed, DownloadState downloadState, double percentage);

    public class DownloadList : Feed
    {
        public MediaType MediaType { get; set; }
        private const int PoolSize = 5;

        [JsonIgnore] public ListDownloadStatusEventHandler OnListDownloadStatusChange;

        public DownloadList(MediaType mediaType, ListDownloadStatusEventHandler onDownloadStatusChange)
        {
            MediaType = mediaType;
            OnListDownloadStatusChange = onDownloadStatusChange;
        }

        public void Download()
        {
            if (ExecutionStatus == ExecutionStatus.Deleted) { Delete(); return; }
            var count = Entries.Count;
            if (count == 0) return;
            var firstEntry = Entries[0] as YoutubeEntry;
            if (firstEntry != null) {
                if (count == 1) Title = firstEntry.Title;
                else { Title = firstEntry.ChannelName; if (string.IsNullOrEmpty(Title)) Title = firstEntry.Title; }
            }
            UpdateStatus(DownloadState.AllStart, null, 0.0);
            foreach (YoutubeEntry item in Entries) item.OnEntryDownloadStatusChange += OnDownloadStatusChanged;
            DownloadFirst();

        }

        private void OnDownloadStatusChanged(Feed feed, DownloadState downloadState, double percentage)
        {
            var finishedCount = 0;
            var downloadCount = 0;
            var average = 0.0;
            var entry = feed as YoutubeEntry;
            if (downloadState == DownloadState.Deleted) {
                if (entry != null) { entry.OnEntryDownloadStatusChange = null; Entries.Remove(entry); }
                return;
            }
            foreach (var en in Entries) {
                if (en.DownloadState == DownloadState.Ready || en.DownloadState == DownloadState.Error) finishedCount++;
                if (!(en.DownloadState == DownloadState.Ready || en.DownloadState == DownloadState.Error || en.DownloadState == DownloadState.Initialized)) downloadCount++;
                average += en.Percentage;
            }
            average = average/Entries.Count;

            if (OnListDownloadStatusChange != null) {
                DownloadState = downloadState;
                if (downloadState == DownloadState.DownloadProgressChanged)  Percentage = average;
                if (downloadCount == 0 && finishedCount == Entries.Count) DownloadState = DownloadState.AllFinished;
                if (Entries.Count == 1 && downloadState == DownloadState.TitleChanged)  Title = Entries[0].Title;
                OnListDownloadStatusChange(this, feed, DownloadState, Percentage);
            }
            if (downloadCount < PoolSize) DownloadFirst();
        }

        private void UpdateStatus(DownloadState state, YoutubeEntry entry, double percentage)
        {
            DownloadState = state;
            Percentage = percentage;
            if (OnListDownloadStatusChange != null) OnListDownloadStatusChange(this, entry, DownloadState, Percentage);
        }

        private void DownloadFirst()
        {
            for (var i = 0; i < Entries.Count; i++) {
                var entry = Entries[i] as YoutubeEntry;
                if (entry == null || entry.DownloadState != DownloadState.Initialized || entry.DownloadState == DownloadState.Deleted) continue;
                entry.UpdateStatus(DownloadState.DownloadStart, 0.0);
                Task.Factory.StartNew(() => entry.DownloadAsync(MediaType));
                break;
            }
        }

        public override void Delete()
        {
            base.Delete();
            foreach (YoutubeEntry youtubeEntry in Entries) 
                youtubeEntry.OnEntryDownloadStatusChange = null;
            Entries.Clear();
            UpdateStatus(DownloadState.Deleted, null, 0.0);
        }
    }
}