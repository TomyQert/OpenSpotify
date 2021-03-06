﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenSpotify.Models;
using OpenSpotify.Services.Util;
using VideoLibrary;
using static OpenSpotify.Services.Util.Utils;

namespace OpenSpotify.Services
{
    public class DownloadService : BaseService
    {
        public DownloadService(ApplicationModel applicationModel,
            NavigationService navigationService) {
            ApplicationModel = applicationModel;
            NavigationService = navigationService;
            InitializeFileWatcher();
        }


        #region Download Songs

        private void DownloadSongs(ItemModel song) {
            if (!IsInternetAvailable())
                return;

            try {
                var video = YouTube.GetVideo(song.YouTubeUri);
                if (video == null) {
                    song.Status = FailedYoutTubeUri;
                    return;
                }

                song.FileName = Path.GetFileNameWithoutExtension(RemoveSpecialCharacters(video.FullName.Replace(" ", string.Empty)));
                File.WriteAllBytes(TempPath + "\\" + RemoveSpecialCharacters(video.FullName.Replace(" ", string.Empty)),video.GetBytes());
                SetStatus(song, Status.Converting);
            }
            catch (Exception ex) {
                SetStatus(song, Status.Failed);
#if !DEBUG
                new LogException(ex);
#endif
                Debug.WriteLine(ex.Message);
                Debug.WriteLine(ex.StackTrace);
            }
        }

        #endregion

        #region Fields

        private YouTubeService _youTubeService;
        private YouTube _youTube;
        private NavigationService _navigationService;
        private ApplicationModel _applicationModel;

        #endregion

        #region Properties

        public ApplicationModel ApplicationModel {
            get { return _applicationModel; }
            set {
                _applicationModel = value;
                OnPropertyChanged(nameof(ApplicationModel));
            }
        }

        public NavigationService NavigationService {
            get { return _navigationService; }
            set {
                _navigationService = value;
                OnPropertyChanged(nameof(NavigationService));
            }
        }

        public YouTubeService YouTubeService {
            get { return _youTubeService; }
            set {
                _youTubeService = value;
                OnPropertyChanged(nameof(YouTubeService));
            }
        }

        public YouTube YouTube {
            get { return _youTube; }
            set {
                _youTube = value;
                OnPropertyChanged(nameof(YouTube));
            }
        }

        public ConvertService ConvertService { get; set; }

        public FileSystemWatcher FileSystemDownloadWatcher { get; set; }

        public FileSystemWatcher FileSystemMusicWatcher { get; set; }

        public DateTime LastDownload { get; set; } = DateTime.MinValue;

        public DateTime LastMusic { get; set; } = DateTime.MinValue;

        #endregion


        private void InitializeFileWatcher() {
            FileSystemDownloadWatcher = new FileSystemWatcher(TempPath) {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
                Filter = "*.*"
            };

            FileSystemMusicWatcher = new FileSystemWatcher(MusicPath) {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
                Filter = "*.*"
            };
            FileSystemDownloadWatcher.Created += FileSystemDownloadWatcherOnCreated;
            FileSystemMusicWatcher.Created += FileSystemMusicWatcherOnCreated;
        }

        public async void Start(string songId) {
            try {
                if(!IsInternetAvailable()) {
                    ApplicationModel.StatusText = NoInternet;
                    return;
                }

                //var song = SearchSpotifySongInformation(songId);
                //song.Status = LoadingSongInformation;
                //song.YouTubeUri = await SearchForSong(song.SongName, song.Artists?[0]);

                //if (string.IsNullOrEmpty(song.YouTubeUri) ||
                //   song.YouTubeUri.Length <= YouTubeUri.Length) {
                //    song.Status = FailedLoadingSongInformation;
                //    return;
                //}

                //Application.Current.Dispatcher.Invoke(() => {
                //    if(ApplicationModel.DownloadCollection.Any(i => i.Id == song.Id)) {
                //        return;
                //    }

                //    SetStatus(song, Status.Downloading);
                //    ApplicationModel.DownloadCollection.Add(song);
                //});

                //DownloadSongs(song);
            }
            catch (Exception ex) {
#if !DEBUG
                new LogException(ex);
#endif
                Debug.Write(ex.Message);
                Debug.Write(ex.StackTrace);
            }
        }

        //#endregion

        #region SearchQuery YouTube 

        public async Task<string> SearchForSong(string songName, string artist) {
            try {
                var searchListRequest = YouTubeService.Search.List(SearchInfo);
                searchListRequest.Q = $"{songName} {artist}"; // SearchQuery Term
                searchListRequest.MaxResults = 50;

                var searchListResponse = await searchListRequest.ExecuteAsync();

                var matchingItems = searchListResponse.Items.Where(x =>
                    x.Snippet.Title.Contains(songName, StringComparison.OrdinalIgnoreCase) ||
                    x.Snippet.Title.Contains(artist, StringComparison.OrdinalIgnoreCase)).ToList();

                if (matchingItems.Count == 0)
                    return string.Empty;

                //Checks Content for VEVO
                if (matchingItems.Count > 1) {
                    var vevoMatches = matchingItems.Where(x => x.Snippet.ChannelTitle.Contains(Vevo)).ToList();
                    if (vevoMatches.Count > 0)
                        return YouTubeUri + CheckVevoMatches(vevoMatches).Id.VideoId;
                }

                return YouTubeUri + matchingItems[0].Id.VideoId;
            }
            catch (Exception ex) {
                new LogException(ex);
                return null;
            }
        }

        private SearchResult CheckVevoMatches(IReadOnlyList<SearchResult> vevoResults) {
            var result = vevoResults.FirstOrDefault(x => x.Snippet.Title.Contains(Audio));
            return result ?? vevoResults[0];
        }

        #endregion

        #region FileSystemWatcher

        /// <summary>
        ///     Notifys when File Converted and Done
        ///     lastWriteTime.Subtract(LastDownload).Ticks > 0 because the FileSystemWatcher gets called 2 times read here
        ///     http://stackoverflow.com/questions/1764809/filesystemwatcher-changed-event-is-raised-twice?answertab=votes#tab-top
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="fileSystemEventArgs"></param>
        private void FileSystemMusicWatcherOnCreated(object sender, FileSystemEventArgs fileSystemEventArgs) {
            try {
                var lastWriteTime = File.GetLastWriteTime(fileSystemEventArgs.FullPath);
                if (lastWriteTime.Subtract(LastMusic).Ticks > 0) {
                    var finishedSong = ApplicationModel.DownloadCollection.FirstOrDefault(
                        x =>
                            x.FileName.Contains(Path.GetFileNameWithoutExtension(fileSystemEventArgs.Name),
                                StringComparison.OrdinalIgnoreCase));
                    if (finishedSong == null)
                        return;

                    Application.Current.Dispatcher.Invoke(() => {
                        ConvertService.KillFFmpegProcess();

                        var fullPath = Path.Combine(MusicPath, Path.GetFileName(fileSystemEventArgs.FullPath));
                        finishedSong.FullPath = fullPath;
                        SetStatus(finishedSong, Status.Done);

                        if (ApplicationModel.DownloadCollection.Count == 1) {
                            ApplicationModel.StatusText = "Ready...";

                            if (ApplicationModel.Settings.RemoveSongsFromList)
                                ApplicationModel.DownloadCollection.Remove(finishedSong);
                            ApplicationModel.SongCollection.Add(finishedSong);
                            NavigationService.ContentWindow = NavigationService.SpotifyView;
                        }
                        else {
                            if (ApplicationModel.Settings.RemoveSongsFromList)
                                ApplicationModel.DownloadCollection.Remove(finishedSong);
                            ApplicationModel.StatusText = $"Downloading {ApplicationModel.DownloadCollection.Count}/" +
                                                          $"{ApplicationModel.DroppedSongs.Count}";
                            ApplicationModel.SongCollection.Add(finishedSong);
                        }
                    });
                }
            }
            catch (Exception ex) {
                Debug.Write(ex.Message);
                Debug.Write(ex.StackTrace);
            }
        }

        /// <summary>
        ///     Notifys when Download is finished ||
        ///     lastWriteTime.Subtract(LastDownload).Ticks > 0 because the FileSystemWatcher gets called 2 times read here
        ///     http://stackoverflow.com/questions/1764809/filesystemwatcher-changed-event-is-raised-twice?answertab=votes#tab-top
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="fileSystemEventArgs"></param>
        private void FileSystemDownloadWatcherOnCreated(object sender, FileSystemEventArgs fileSystemEventArgs) {
            try {
                var lastWriteTime = File.GetLastWriteTime(fileSystemEventArgs.FullPath);
                if (lastWriteTime.Subtract(LastDownload).Ticks > 0)
                    if (ConvertService == null) {
                        ConvertService = new ConvertService(ApplicationModel) {
                            SongFileName = fileSystemEventArgs.FullPath
                        };
                        ConvertService.StartFFmpeg();
                    }
                    else {
                        ConvertService.SongFileName = fileSystemEventArgs.FullPath;
                        ConvertService.StartFFmpeg();
                    }
            }
            catch (Exception ex) {
                new LogException(ex);
            }
        }

        #endregion

        public void AddFailedSong(string artist, string songName) {

            var song = new ItemModel {
                SongName = songName,
                ArtistName = artist
            };
            ApplicationModel.DownloadCollection.Add(song);
            SetStatus(song, Status.Failed);
        }
    }
}