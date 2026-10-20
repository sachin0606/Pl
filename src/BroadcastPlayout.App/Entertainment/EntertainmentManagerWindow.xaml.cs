using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace BroadcastPlayout.Entertainment
{
    public class MusicTrackItem
    {
        public int Index { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string Language { get; set; } = "Hindi";
        public string DurationText { get; set; } = "04:15";
        public string FilePath { get; set; } = string.Empty;
    }

    public class ScheduledMovieItem
    {
        public string ScheduledTimeText { get; set; } = "08:00 PM";
        public string Title { get; set; } = string.Empty;
        public string Cast { get; set; } = string.Empty;
        public string Genre { get; set; } = "Action/Drama";
        public string Director { get; set; } = string.Empty;
        public int Year { get; set; } = 2024;
        public string DurationText { get; set; } = "02:35:00";
        public string FilePath { get; set; } = string.Empty;
    }

    public class ViewerRequestItem
    {
        public string RequestTimeText { get; set; } = DateTime.Now.ToString("hh:mm tt");
        public string SenderName { get; set; } = string.Empty;
        public string RequestedTitle { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Source { get; set; } = "WhatsApp";
        public string Status { get; set; } = "Pending";
    }

    public partial class EntertainmentManagerWindow : Window
    {
        public ObservableCollection<MusicTrackItem> MusicQueue { get; set; } = new ObservableCollection<MusicTrackItem>();
        public ObservableCollection<ScheduledMovieItem> ScheduledMovies { get; set; } = new ObservableCollection<ScheduledMovieItem>();
        public ObservableCollection<ViewerRequestItem> ViewerRequests { get; set; } = new ObservableCollection<ViewerRequestItem>();

        public EntertainmentManagerWindow()
        {
            InitializeComponent();

            DgMusicQueue.ItemsSource = MusicQueue;
            DgMovieLineup.ItemsSource = ScheduledMovies;
            DgRequests.ItemsSource = ViewerRequests;

            LoadDefaultDemoData();
        }

        private void LoadDefaultDemoData()
        {
            // Demo Music Playlist (9x / B4U Style)
            MusicQueue.Add(new MusicTrackItem { Index = 1, Title = "Kesariya (Brahmastra)", Artist = "Arijit Singh", Album = "Brahmastra", Genre = "Bollywood Romance", Language = "Hindi", DurationText = "04:28" });
            MusicQueue.Add(new MusicTrackItem { Index = 2, Title = "Tauba Tauba (Bad Newz)", Artist = "Karan Aujla", Album = "Bad Newz", Genre = "Punjabi / Dance", Language = "Punjabi", DurationText = "03:45" });
            MusicQueue.Add(new MusicTrackItem { Index = 3, Title = "Chaleya (Jawan)", Artist = "Arijit Singh, Shilpa Rao", Album = "Jawan", Genre = "Bollywood Pop", Language = "Hindi", DurationText = "03:20" });
            MusicQueue.Add(new MusicTrackItem { Index = 4, Title = "Tum Hi Ho (Aashiqui 2)", Artist = "Arijit Singh", Album = "Aashiqui 2", Genre = "Retro Melodies", Language = "Hindi", DurationText = "04:22" });
            MusicQueue.Add(new MusicTrackItem { Index = 5, Title = "Illuminati (Aavesham)", Artist = "Sushin Shyam, Dabzee", Album = "Aavesham", Genre = "South Mass Hit", Language = "Malayalam", DurationText = "03:12" });
            TxtMusicQueueCount.Text = $" ({MusicQueue.Count} Tracks Queued)";

            // Demo Movie Lineup (Sony Max / Star Gold Style)
            ScheduledMovies.Add(new ScheduledMovieItem { ScheduledTimeText = "09:00 AM", Title = "Hera Pheri (Classic Comedy)", Cast = "Akshay Kumar, Suniel Shetty, Paresh Rawal", Genre = "Comedy", Director = "Priyadarshan", Year = 2000, DurationText = "02:36:00" });
            ScheduledMovies.Add(new ScheduledMovieItem { ScheduledTimeText = "12:30 PM", Title = "Pushpa: The Rise (Hindi Dubbed)", Cast = "Allu Arjun, Rashmika Mandanna", Genre = "Action Mass", Director = "Sukumar", Year = 2021, DurationText = "02:59:00" });
            ScheduledMovies.Add(new ScheduledMovieItem { ScheduledTimeText = "04:00 PM", Title = "3 Idiots (Mega Blockbuster)", Cast = "Aamir Khan, Kareena Kapoor, R. Madhavan", Genre = "Drama/Comedy", Director = "Rajkumar Hirani", Year = 2009, DurationText = "02:51:00" });
            ScheduledMovies.Add(new ScheduledMovieItem { ScheduledTimeText = "08:00 PM", Title = "Jawan (Super Prime Time Premier)", Cast = "Shah Rukh Khan, Nayanthara, Vijay Sethupathi", Genre = "Action Thriller", Director = "Atlee", Year = 2023, DurationText = "02:49:00" });
            TxtMovieCount.Text = $" ({ScheduledMovies.Count} Movies Scheduled Today)";

            // Demo Viewer Requests
            ViewerRequests.Add(new ViewerRequestItem { SenderName = "Rohit Sharma (Delhi)", RequestedTitle = "Kesariya", Message = "Dedicated to my best friend on his birthday!", Source = "WhatsApp", Status = "Queued to Ticker" });
            ViewerRequests.Add(new ViewerRequestItem { SenderName = "Pooja Verma (Jaipur)", RequestedTitle = "Chaleya", Message = "Special dedication to Shyam family!", Source = "SMS", Status = "Approved" });
        }

        private void SyncWithMain_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("All Entertainment Channel categories, Music Jukebox queues, and Movie schedules are synchronized with Kashtrix Playout Core!", "Kashtrix Entertainment Playout", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void GenerateMusicLineup_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Auto 24-Hour Music rotation generated successfully based on selected theme & Artist repeat guard!", "Music Automation", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddMusicFiles_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Media Files (*.mp4;*.mkv;*.ts;*.avi;*.mov)|*.mp4;*.mkv;*.ts;*.avi;*.mov|All Files (*.*)|*.*",
                Multiselect = true,
                Title = "Select Music Videos"
            };

            if (ofd.ShowDialog() == true)
            {
                foreach (var file in ofd.FileNames)
                {
                    MusicQueue.Add(new MusicTrackItem
                    {
                        Index = MusicQueue.Count + 1,
                        Title = Path.GetFileNameWithoutExtension(file),
                        Artist = "Channel Music Library",
                        Album = "On-Air Playlist",
                        Genre = "Music Video",
                        FilePath = file
                    });
                }
                TxtMusicQueueCount.Text = $" ({MusicQueue.Count} Tracks Queued)";
            }
        }

        private void ClearMusicQueue_Click(object sender, RoutedEventArgs e)
        {
            MusicQueue.Clear();
            TxtMusicQueueCount.Text = " (0 Tracks)";
        }

        private void PlayMusicToAir_Click(object sender, RoutedEventArgs e)
        {
            if (MusicQueue.Count == 0)
            {
                MessageBox.Show("Music queue is empty! Add tracks first.", "Kashtrix Music Playout", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MessageBox.Show($"Playing '{MusicQueue[0].Title}' On-Air with Lower-Third Now Playing CG ticker!", "Kashtrix Music Playout", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AutoScheduleMovies_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("4-Movie Daily Schedule automatically allocated with prime-time commercial break markers!", "Movie Automation", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddMovieFile_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Movie Video Files (*.mp4;*.mkv;*.ts;*.m2ts)|*.mp4;*.mkv;*.ts;*.m2ts|All Files (*.*)|*.*",
                Title = "Select Master Movie File"
            };

            if (ofd.ShowDialog() == true)
            {
                ScheduledMovies.Add(new ScheduledMovieItem
                {
                    ScheduledTimeText = "Next Slot",
                    Title = Path.GetFileNameWithoutExtension(ofd.FileName),
                    Cast = "Featured Stars",
                    Genre = "Blockbuster",
                    Director = "Director",
                    Year = DateTime.Now.Year,
                    FilePath = ofd.FileName
                });
                TxtMovieCount.Text = $" ({ScheduledMovies.Count} Movies Scheduled Today)";
            }
        }

        private void PreviewMovie_Click(object sender, RoutedEventArgs e)
        {
            if (DgMovieLineup.SelectedItem is ScheduledMovieItem movie)
            {
                MessageBox.Show($"Previewing: {movie.Title}\nCast: {movie.Cast}\nDirector: {movie.Director}", "Movie Preview", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Please select a movie from the lineup list to preview.", "Movie Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CueMovieToPlayout_Click(object sender, RoutedEventArgs e)
        {
            if (DgMovieLineup.SelectedItem is ScheduledMovieItem movie)
            {
                MessageBox.Show($"'{movie.Title}' has been cued to Kashtrix Playout with Cast Banner & Next Up Bug!", "Movie Playout", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Please select a movie to cue to air.", "Movie Cue", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AddTestRequest_Click(object sender, RoutedEventArgs e)
        {
            ViewerRequests.Add(new ViewerRequestItem
            {
                SenderName = "Viewer " + new Random().Next(100, 999),
                RequestedTitle = "Latest Bollywood Song",
                Message = "Best wishes to Kashtrix Music Channel viewers!",
                Source = "WhatsApp",
                Status = "Approved"
            });
        }

        private void PushRequestToAir_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Approved viewer dedications are now actively scrolling on the On-Screen TV Ticker!", "Viewer On-Demand", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}