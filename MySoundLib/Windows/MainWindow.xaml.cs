﻿using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Security.Cryptography;
using WMPLib;
using MySoundLib.Configuration;
using MySoundLib.UserControls.List;
using MySoundLib.UserControls.Create;
using System.Data;

namespace MySoundLib.Windows
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        public ServerConnectionManager ConnectionManager { get; private set; }
        private int _currentSongId;
        private WindowsMediaPlayer _mediaPlayer;
        private DispatcherTimer _updateProgressTimer;
        private PlayState _playState = PlayState.Start;

        public MainWindow()
        {
            // connect  to database if autoconnect is active
            if (Settings.Contains(Property.AutoConnect) && Settings.Contains(Property.LastServer) &&
                Settings.Contains(Property.LastUser))
            {
                ConnectionManager = new ServerConnectionManager();
                ConnectionManager.Connect(Settings.GetValue(Property.LastServer), Settings.GetValue(Property.LastUser),
                    LoginWindow.GetDecryptedPassword(), "my_sound_lib");
            }
            else
            {
                if (!ShowLoginWindow())
                {
                    Close();
                    return;
                }
            }

            InitializeComponent();
            InitialiseFirstView();
        }

        private void InitialiseFirstView()
        {
            // show upload-song control when no songs exist
            var songExists = ConnectionManager.ExecuteScalar(CommandFactory.GetSongAmount());
            int amountSongs;

            if (songExists == null || !int.TryParse(songExists.ToString(), out amountSongs))
            {
                Debug.WriteLine("unable to retrieve if there are songs");
                return;
            }

            if (amountSongs == 0)
            {
                GridContent.Children.Clear();
                GridContent.Children.Add(new UserControlUploadSong(this));
            }
            else
            {
                ListBoxCategory.SelectedIndex = 0;
            }

            UpdatePlaylists();

            // decide visibility of menu
            if (Settings.Contains(Property.CollapseMenu))
            {
                HideMenu();
            } // is shown by default

            HideCurrentSong();
        }

        public void UpdatePlaylists()
        {
            var playlists = ConnectionManager.GetDataTable(CommandFactory.GetPlaylistNames());
            ListBoxPlaylists.Items.Clear();
            foreach (DataRow x in playlists.Rows)
            {
                ListBoxPlaylists.Items.Add(new { name=x["name"], playlist_id=x["playlist_id"]});
            }
        }

        private bool ShowLoginWindow()
        {
            var loginWindow = new LoginWindow();
            loginWindow.ShowDialog();

            if (loginWindow.ResultConnectionManager == null) return false;
            ConnectionManager = loginWindow.ResultConnectionManager;

            return true;
        }

        private void MenuItemTestingWindow_OnClick(object sender, RoutedEventArgs e)
        {
            var testWindow = new TestWindow(ConnectionManager) { Owner = this };

            testWindow.Show();
        }

        private void MenuItemDisconnect_OnClick(object sender, RoutedEventArgs e)
        {
            Hide();
            ConnectionManager.Disconnect();
            if (ShowLoginWindow())
            {
                InitialiseFirstView();
                Show();
            }
            else
                Close();
        }

        private void MenuItemSettings_OnClick(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new WindowSettings();
            settingsWindow.ShowDialog();
        }

        private void MenuItemAbout_OnClick(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow(ConnectionManager) { Owner = this };

            aboutWindow.ShowDialog();
        }

        public void HideCurrentSong()
        {
            StackPanelCurrentSongInformation.Visibility = Visibility.Collapsed;
            GridSplitterHor.Visibility = Visibility.Collapsed;
            Grid.SetRowSpan(GridContent, 3);
        }

        public void ShowCurrentSong()
        {
            StackPanelCurrentSongInformation.Visibility = Visibility.Visible;
            GridSplitterHor.Visibility = Visibility.Visible;
            Grid.SetRowSpan(GridContent, 1);
        }

        private void ListBoxItemSongs_OnSelected(object sender, RoutedEventArgs e)
        {
            ResetPlaylistList();
            GridContent.Children.Add(new UserControlSongs(this));
        }

        private void ListBoxItemAlbums_OnSelected(object sender, RoutedEventArgs e)
        {
            ResetPlaylistList();
            GridContent.Children.Add(new UserControlAlbums(this));
        }

        private void ListBoxItemArtists_OnSelected(object sender, RoutedEventArgs e)
        {
            ResetPlaylistList();
            GridContent.Children.Add(new UserControlArtists(this));
        }

        private void ListBoxItemGenres_OnSelected(object sender, RoutedEventArgs e)
        {
            ResetPlaylistList();
            GridContent.Children.Add(new UserControlGenres(this));
        }

        private void ResetPlaylistList()
        {
            ListBoxPlaylists.SelectedIndex = -1;
            GridContent.Children.Clear();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            ConnectionManager.Disconnect();
            Settings.SaveConfig();
        }

        public void PlaySong(int id)
        {
            _mediaPlayer?.close();

            ShowCurrentSong();
            _currentSongId = id;
            var song = ConnectionManager.GetDataTable(CommandFactory.GetSongInformation(id));

            var title = song.Rows[0]["song_title"];

            LabelSongTitle.Content = "Title: " + title;

            Debug.WriteLine("moving to play track");
            Task task = new Task(PlayTrack);

            task.Start(TaskScheduler.FromCurrentSynchronizationContext());
            Debug.WriteLine("end play");
        }

        private void ButtonPlay_OnClick(object sender, RoutedEventArgs e)
        {
            switch (_playState)
            {
                case PlayState.Start:
                    PlayTrack();
                    return;
                case PlayState.Pause:
                    _mediaPlayer.controls.pause();
                    _playState = PlayState.Continue;
                    ImagePlay.Source = (System.Windows.Media.Imaging.BitmapImage)FindResource("ImagePlay");
                    return;
                case PlayState.Continue:
                    _mediaPlayer.controls.play();
                    _playState = PlayState.Pause;
                    ImagePlay.Source = (System.Windows.Media.Imaging.BitmapImage)FindResource("ImagePause");
                    return;
            }
        }

        private void PlayTrack()
        {
            Debug.WriteLine("Loading track from song_id " + _currentSongId);
            var track = ConnectionManager.GetDataTable("SELECT track FROM songs WHERE song_id = " + _currentSongId);

            var byteTrack = (byte[])track.Rows[0]["track"];

            string hash;
            using (var md5 = new SHA1CryptoServiceProvider())
            {
                hash = Convert.ToBase64String(md5.ComputeHash(byteTrack));
            }
            var pathFile = Path.Combine(Settings.PathProgramFolder, hash.Replace('/', '_') + ".mp3");

            if (File.Exists(pathFile))
            {
                Debug.WriteLine("Already played song");
            }
            else
            {
                try
                {
                    var fileStream = new FileStream(pathFile, FileMode.Create, FileAccess.Write);

                    fileStream.Write(byteTrack, 0, byteTrack.Length);

                    fileStream.Close();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("unable to save file: ", ex.ToString());
                    return;
                }
            }

            _mediaPlayer = new WindowsMediaPlayer { URL = pathFile };

            _mediaPlayer.PlayStateChange += MediaPlayerOnPlayStateChange;

            _mediaPlayer.controls.play();

            _updateProgressTimer = new DispatcherTimer();
            _updateProgressTimer.Tick += UpdateProgressTimerOnTick;
            _updateProgressTimer.Interval = new TimeSpan(0, 0, 0, 0, 500);
            _updateProgressTimer.Start();

            Debug.WriteLine("Playing song");
            _playState = PlayState.Pause;
            ImagePlay.Source = (System.Windows.Media.Imaging.BitmapImage)FindResource("ImagePause");
        }

        private void UpdateProgressTimerOnTick(object sender, EventArgs eventArgs)
        {
            var duration = _mediaPlayer?.currentMedia?.duration;

            if (duration != null && (int)duration != 0)
            {
                ProgressBarTrack.Maximum = (double)duration;
                ProgressBarTrack.Value = _mediaPlayer.controls.currentPosition;
            }
        }

        private void MediaPlayerOnPlayStateChange(int newState)
        {
            if (newState == (int)WMPPlayState.wmppsStopped)
            {
                _currentSongId = 0;
                _mediaPlayer = null;
                ProgressBarTrack.Value = 0;
                HideCurrentSong();

                var userControlSongs = GridContent.Children[0] as UserControlSongs;

                userControlSongs?.ResetBackgroundFromRecentSong();
            }
        }

        private void ButtonMute_OnClick(object sender, RoutedEventArgs e)
        {
            if (ButtonMute.Content.ToString() == "Unmute")
            {
                _mediaPlayer.settings.mute = false;
                ButtonMute.Content = "Mute";
                return;
            }
            _mediaPlayer.settings.mute = true;
            ButtonMute.Content = "Unmute";
        }

        private void ButtonRestart_OnClick(object sender, RoutedEventArgs e)
        {
            _mediaPlayer.controls.currentPosition = 0;
        }

        private void ProgressBarTrack_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            double value = GetProgressBarValue(e.GetPosition(ProgressBarTrack).X);

            ProgressBarTrack.Value = value;
            _mediaPlayer.controls.currentPosition = value;
        }

        private double GetProgressBarValue(double MousePosition)
        {
            double ratio = MousePosition / ProgressBarTrack.ActualWidth;
            double ProgressBarValue = ratio * ProgressBarTrack.Maximum;
            return ProgressBarValue;
        }

        private void MenuItemHideMenu_Click(object sender, RoutedEventArgs e)
        {
            HideMenu();
            Settings.SetProperty(Property.CollapseMenu, "yes");
        }

        private void ButtonShowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowMenu();
            Settings.RemoveProperty(Property.CollapseMenu);
        }

        private void HideMenu()
        {
            AppMenu.Visibility = Visibility.Collapsed;
            ButtonShowMenuItem.Visibility = Visibility.Visible;
        }

        private void ShowMenu()
        {
            AppMenu.Visibility = Visibility.Visible;
            ButtonShowMenuItem.Visibility = Visibility.Collapsed;
        }

        private void ButtonCreatePlaylist_Click(object sender, RoutedEventArgs e)
        {
            GridContent.Children.Clear();
            ListBoxCategory.UnselectAll();
            GridContent.Children.Add(new UserControlCreatePlaylist(this));
        }

        private void ListBox_Selected(object sender, RoutedEventArgs e)
        {
            GridContent.Children.Clear();
            ListBoxCategory.SelectedIndex = -1;

            var item = (sender as ListBoxItem).Content;

            GridContent.Children.Add(new UserControlPlaylists(this, int.Parse(item.GetType().GetProperty("playlist_id").GetValue(item).ToString())));
        }
    }

    public enum PlayState
    {
        Start,
        Pause,
        Continue
    }

    public class MillisecondsToValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int milliseconds;
            if (int.TryParse(value.ToString(), out milliseconds))
            {
                var ts = TimeSpan.FromMilliseconds(milliseconds);

                return ts.ToString("mm':'ss");
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
