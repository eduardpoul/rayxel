using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace Rayxel.Send.App;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _model;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Follow(DataContext as MainViewModel);
    }

    private void Follow(MainViewModel? model)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
    }

    // A pairing waits on the user, who may have scrolled down into the settings: bring the
    // PIN, which sits near the top, into view when one starts.
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPairing) && _model is { IsPairing: true })
            Scroller.Offset = new Vector(Scroller.Offset.X, 0);
    }
}
