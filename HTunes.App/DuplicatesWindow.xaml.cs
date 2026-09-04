using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HTunes.App
{
    public class DuplicateTrackItem : INotifyPropertyChanged
    {
        public Track Track { get; }
        private bool shouldDelete;
        public bool ShouldDelete
        {
            get => shouldDelete;
            set { shouldDelete = value; OnPropertyChanged(); }
        }

        public DuplicateTrackItem(Track track)
        {
            Track = track;
            ShouldDelete = false;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class DuplicatesWindow : Window
    {
        private readonly List<List<Track>> duplicateSets;
        private int currentSetIndex = 0;
        private readonly List<Track> tracksToDelete = new();

        public List<Track> TracksToDelete => tracksToDelete;

        public DuplicatesWindow(List<List<Track>> duplicates)
        {
            InitializeComponent();
            duplicateSets = duplicates;
            LoadCurrentSet();
        }

        private void LoadCurrentSet()
        {
            if (currentSetIndex >= duplicateSets.Count)
            {
                DialogResult = true;
                Close();
                return;
            }

            var currentSet = duplicateSets[currentSetIndex];
            StatusText.Text = $"Set {currentSetIndex + 1} of {duplicateSets.Count}: '{currentSet.First().Title}'";
            DuplicatesGrid.ItemsSource = currentSet.Select(t => new DuplicateTrackItem(t)).ToList();
            DeleteNextButton.Content = currentSetIndex == duplicateSets.Count - 1 ? "Delete selected & Finish" : "Delete selected & Next";
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            currentSetIndex++;
            LoadCurrentSet();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DeleteNext_Click(object sender, RoutedEventArgs e)
        {
            if (DuplicatesGrid.ItemsSource is List<DuplicateTrackItem> items)
            {
                tracksToDelete.AddRange(items.Where(i => i.ShouldDelete).Select(i => i.Track));
            }

            currentSetIndex++;
            LoadCurrentSet();
        }
    }
}
