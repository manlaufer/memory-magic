using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MemoryMagic.ViewModels;

namespace MemoryMagic;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isExplicitExit;
    private bool _hasShownTrayBalloon;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(RestoreFromTray, HideToTray, ExitApplication);
        DataContext = _viewModel;
        Icon = _viewModel.WindowIcon;

        Loaded += (_, _) =>
        {
            RecalculateDebloaterGridLayout();
            if (App.StartMinimized)
                Hide();
        };
        Activated += (_, _) => RecalculateDebloaterGridLayout();
        DpiChanged += (_, _) => RecalculateDebloaterGridLayout();
        _viewModel.DebloatItems.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(RecalculateDebloaterGridLayout, DispatcherPriority.Loaded);
    }

    private void OnMainTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == MainTabs)
            Dispatcher.BeginInvoke(RecalculateDebloaterGridLayout, DispatcherPriority.Loaded);
    }

    private void RecalculateDebloaterGridLayout()
    {
        if (DebloaterGrid is null || DebloaterGrid.Columns.Count < 8)
            return;

        // Enforce explicit MinWidth + star weights after elevation, DPI shifts, or tab activation
        DebloaterGrid.Columns[0].Width = new DataGridLength(56);
        DebloaterGrid.Columns[0].MinWidth = 56;
        DebloaterGrid.Columns[1].Width = new DataGridLength(1.6, DataGridLengthUnitType.Star);
        DebloaterGrid.Columns[1].MinWidth = 170;
        DebloaterGrid.Columns[2].Width = new DataGridLength(115);
        DebloaterGrid.Columns[2].MinWidth = 105;
        DebloaterGrid.Columns[3].Width = new DataGridLength(105);
        DebloaterGrid.Columns[3].MinWidth = 95;
        DebloaterGrid.Columns[4].Width = new DataGridLength(100);
        DebloaterGrid.Columns[4].MinWidth = 90;
        DebloaterGrid.Columns[5].Width = new DataGridLength(1.1, DataGridLengthUnitType.Star);
        DebloaterGrid.Columns[5].MinWidth = 125;
        DebloaterGrid.Columns[6].Width = new DataGridLength(2.1, DataGridLengthUnitType.Star);
        DebloaterGrid.Columns[6].MinWidth = 200;
        DebloaterGrid.Columns[7].Width = new DataGridLength(96);
        DebloaterGrid.Columns[7].MinWidth = 96;

        DebloaterGrid.InvalidateMeasure();
        DebloaterGrid.UpdateLayout();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        Hide();
        if (!_hasShownTrayBalloon)
        {
            _hasShownTrayBalloon = true;
            _viewModel.NotifyMinimizedToTray();
        }
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isExplicitExit)
        {
            base.OnClosing(e);
            return;
        }

        string behavior = _viewModel.SelectedCloseBehavior;
        if (behavior == "Run in Background (Tray)")
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (behavior == "Exit Completely")
        {
            base.OnClosing(e);
            return;
        }

        // Ask Every Time: prompt user for Run in Background (Tray) vs Exit Completely
        (CloseChoice choice, bool remember) = ShowClosePromptDialog();
        if (choice == CloseChoice.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (remember)
        {
            _viewModel.SelectedCloseBehavior = choice == CloseChoice.Tray
                ? "Run in Background (Tray)"
                : "Exit Completely";
        }

        if (choice == CloseChoice.Tray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _viewModel.DisposeAsync();
        Application.Current?.Shutdown();
    }

    private (CloseChoice Choice, bool Remember) ShowClosePromptDialog()
    {
        CloseChoice result = CloseChoice.Cancel;

        var dialog = new Window
        {
            Title = "Close Memory Magic",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(17, 21, 28)),
            Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            WindowStyle = WindowStyle.ToolWindow
        };

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "How would you like to close Memory Magic?",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Running in the background keeps the System Tray monitor and Telemetry Bar active.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var rememberCheck = new CheckBox
        {
            Content = "Remember my choice",
            Margin = new Thickness(0, 0, 0, 14)
        };
        root.Children.Add(rememberCheck);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var trayBtn = new Button
        {
            Content = "Run in Background (Tray)",
            Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        trayBtn.Click += (_, _) => { result = CloseChoice.Tray; dialog.Close(); };

        var exitBtn = new Button
        {
            Content = "Exit Completely",
            Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            Margin = new Thickness(0, 0, 8, 0)
        };
        exitBtn.Click += (_, _) => { result = CloseChoice.Exit; dialog.Close(); };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => { result = CloseChoice.Cancel; dialog.Close(); };

        btnRow.Children.Add(trayBtn);
        btnRow.Children.Add(exitBtn);
        btnRow.Children.Add(cancelBtn);
        root.Children.Add(btnRow);

        dialog.Content = root;
        dialog.ShowDialog();
        return (result, rememberCheck.IsChecked == true);
    }

    private enum CloseChoice
    {
        Cancel,
        Tray,
        Exit
    }
}
