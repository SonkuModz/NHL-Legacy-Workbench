using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NhlWorkbench.App.ViewModels;

namespace NhlWorkbench.App.Views;

public partial class VideosView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private VideosViewModel? _viewModel;
    private bool _isPlaying;
    private bool _isSeeking;

    public VideosView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => ShowPosition();
        Seek.ValueChanged += (_, _) =>
        {
            // A click on the bar jumps straight there. A drag waits until it's let go.
            if (!_isSeeking && Math.Abs(Seek.Value - Player.Position.TotalSeconds) > 0.5)
            {
                Player.Position = TimeSpan.FromSeconds(Seek.Value);
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as VideosViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            ShowFile();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Player.Stop();
        Player.Source = null;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideosViewModel.PlayFile))
        {
            ShowFile();
        }
    }

    private void ShowFile()
    {
        string? file = _viewModel?.PlayFile;
        Player.Stop();
        Player.Source = file is null ? null : new Uri(file);
        NotReady.Visibility = file is null && _viewModel?.HasVideo == true ? Visibility.Visible : Visibility.Collapsed;
        _isPlaying = file is not null;
        PlayButton.Content = _isPlaying ? "Pause" : "Play";
        if (file is null)
        {
            _timer.Stop();
            Position.Text = string.Empty;
            Seek.Value = 0;
        }
        else
        {
            Player.Play();
            _timer.Start();
        }
    }

    private void ShowPosition()
    {
        if (!Player.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        TimeSpan total = Player.NaturalDuration.TimeSpan;
        Seek.Maximum = Math.Max(total.TotalSeconds, 0.1);
        if (!_isSeeking)
        {
            Seek.Value = Player.Position.TotalSeconds;
        }

        Position.Text = string.Create(CultureInfo.InvariantCulture, $"{Player.Position:m\\:ss} / {total:m\\:ss}");
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e) => ShowPosition();

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        // Back to the start, ready to go again.
        Player.Stop();
        _isPlaying = false;
        PlayButton.Content = "Play";
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Problem = $"The player couldn't open this movie, but it can still be saved as an MP4. {e.ErrorException?.Message}";
        }
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null)
        {
            return;
        }

        if (_isPlaying)
        {
            Player.Pause();
        }
        else
        {
            Player.Play();
        }

        _isPlaying = !_isPlaying;
        PlayButton.Content = _isPlaying ? "Pause" : "Play";
    }

    private void OnSeekStarted(object sender, RoutedEventArgs e) => _isSeeking = true;

    private void OnSeekCompleted(object sender, RoutedEventArgs e)
    {
        _isSeeking = false;
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
    }

    private void OnSaveFrame(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SaveFrameCommand.CanExecute(Player.Position.TotalSeconds) == true)
        {
            _viewModel.SaveFrameCommand.Execute(Player.Position.TotalSeconds);
        }
    }
}
