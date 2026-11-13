using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace MemoryMagic.UI;

public interface IExecutionReporter
{
    void SetProgress(double percent, string currentStep);
    void Log(string message);
}

public sealed record ExecutionOutcome(
    bool Succeeded,
    string Headline,
    string Details);

public partial class ExecutionProgressDialog : Window, IExecutionReporter
{
    private readonly Func<IExecutionReporter, CancellationToken, Task<ExecutionOutcome>> _work;
    private readonly CancellationTokenSource _cts = new();
    private bool _isCompleted;
    private ExecutionOutcome _outcome = new(false, "Canceled", "Operation did not complete.");

    private ExecutionProgressDialog(
        string title,
        string subtitle,
        Func<IExecutionReporter, CancellationToken, Task<ExecutionOutcome>> work)
    {
        InitializeComponent();
        _work = work;
        Title = title;
        TitleText.Text = title;
        SubtitleText.Text = subtitle;

        Loaded += OnLoadedAsync;
    }

    public static ExecutionOutcome RunWithDialog(
        Window? owner,
        string title,
        string subtitle,
        Func<IExecutionReporter, CancellationToken, Task<ExecutionOutcome>> work)
    {
        var dlg = new ExecutionProgressDialog(title, subtitle, work);
        if (owner is not null && owner.IsLoaded && owner.IsVisible)
            dlg.Owner = owner;
        else
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.ShowDialog();
        return dlg._outcome;
    }

    public void SetProgress(double percent, string currentStep)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isCompleted)
                return;

            StepText.Text = currentStep;
            if (percent < 0)
            {
                OperationProgressBar.IsIndeterminate = true;
                PercentText.Text = "";
            }
            else
            {
                double clamped = Math.Clamp(percent, 0, 100);
                OperationProgressBar.IsIndeterminate = false;
                OperationProgressBar.Value = clamped;
                PercentText.Text = $"{clamped:F0}%";
            }
        });
    }

    public void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        string line = $"[{DateTime.Now:HH:mm:ss}] {message.TrimEnd()}{Environment.NewLine}";
        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.AppendText(line);
            LogTextBox.ScrollToEnd();
        });
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            _outcome = await _work(this, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _outcome = new ExecutionOutcome(false, "Operation Canceled", "Execution was canceled by the user.");
            Log("Canceled by user.");
        }
        catch (Exception ex)
        {
            _outcome = new ExecutionOutcome(false, "Execution Error", ex.Message);
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            MarkCompleted(_outcome);
        }
    }

    private void MarkCompleted(ExecutionOutcome outcome)
    {
        _isCompleted = true;
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = outcome.Succeeded ? 100 : OperationProgressBar.Value;
        if (outcome.Succeeded)
            PercentText.Text = "100%";

        StepText.Text = outcome.Succeeded ? "Completed." : "Finished with error(s).";

        ResultBanner.Visibility = Visibility.Visible;
        if (outcome.Succeeded)
        {
            ResultBanner.Background = new SolidColorBrush(Color.FromRgb(6, 78, 59));
            ResultBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            ResultHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(236, 253, 245));
            ResultDetailsText.Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208));
            ResultHeadlineText.Text = $"SUCCESS: {outcome.Headline}";
        }
        else
        {
            ResultBanner.Background = new SolidColorBrush(Color.FromRgb(127, 29, 29));
            ResultBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            ResultHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(254, 242, 242));
            ResultDetailsText.Foreground = new SolidColorBrush(Color.FromRgb(254, 202, 202));
            ResultHeadlineText.Text = $"FAILED: {outcome.Headline}";
        }

        ResultDetailsText.Text = outcome.Details;
        CancelButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        CloseButton.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isCompleted)
            return;

        CancelButton.IsEnabled = false;
        StepText.Text = "Canceling operation...";
        Log("Cancellation requested...");
        _cts.Cancel();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = _outcome.Succeeded;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isCompleted)
        {
            e.Cancel = true;
            OnCancelClick(this, new RoutedEventArgs());
            return;
        }

        _cts.Dispose();
        base.OnClosing(e);
    }
}
