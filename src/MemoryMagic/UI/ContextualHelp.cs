using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MemoryMagic.UI;

/// <summary>
/// Global right-click contextual help system for WPF.
/// Intercepts right-clicks on interactive controls to show plain-language explanations,
/// while passing Shift + Right-Click through to standard OS/control context menus.
/// </summary>
public static class ContextualHelp
{
    private static Popup? _sharedPopup;
    private static ContextualHelpPopup? _popupContent;

    public static readonly DependencyProperty EnableGlobalHelpProperty =
        DependencyProperty.RegisterAttached(
            "EnableGlobalHelp",
            typeof(bool),
            typeof(ContextualHelp),
            new PropertyMetadata(false, OnEnableGlobalHelpChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.RegisterAttached(
            "Title",
            typeof(string),
            typeof(ContextualHelp),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SummaryProperty =
        DependencyProperty.RegisterAttached(
            "Summary",
            typeof(string),
            typeof(ContextualHelp),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ImpactProperty =
        DependencyProperty.RegisterAttached(
            "Impact",
            typeof(string),
            typeof(ContextualHelp),
            new PropertyMetadata(null));

    public static bool GetEnableGlobalHelp(DependencyObject obj) => (bool)obj.GetValue(EnableGlobalHelpProperty);
    public static void SetEnableGlobalHelp(DependencyObject obj, bool value) => obj.SetValue(EnableGlobalHelpProperty, value);

    public static string? GetTitle(DependencyObject obj) => (string?)obj.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject obj, string? value) => obj.SetValue(TitleProperty, value);

    public static string? GetSummary(DependencyObject obj) => (string?)obj.GetValue(SummaryProperty);
    public static void SetSummary(DependencyObject obj, string? value) => obj.SetValue(SummaryProperty, value);

    public static string? GetImpact(DependencyObject obj) => (string?)obj.GetValue(ImpactProperty);
    public static void SetImpact(DependencyObject obj, string? value) => obj.SetValue(ImpactProperty, value);

    private static void OnEnableGlobalHelpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window)
            return;

        if ((bool)e.NewValue)
        {
            window.AddHandler(UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(OnPreviewMouseRightButtonUp), handledEventsToo: true);
            window.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnContextMenuOpening), handledEventsToo: true);
            window.PreviewKeyDown += OnWindowPreviewKeyDown;
            window.Closed += OnWindowClosed;
        }
        else
        {
            window.RemoveHandler(UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(OnPreviewMouseRightButtonUp));
            window.RemoveHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnContextMenuOpening));
            window.PreviewKeyDown -= OnWindowPreviewKeyDown;
            window.Closed -= OnWindowClosed;
        }
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_sharedPopup is not null)
            _sharedPopup.IsOpen = false;
    }

    private static void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _sharedPopup is { IsOpen: true })
        {
            _sharedPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Allow Shift + Right-Click to open standard OS/control context menus (e.g., TextBox Copy/Paste).
        if (IsShiftHeld())
            return;

        if (FindHelpTarget(e.OriginalSource as DependencyObject) is not null)
        {
            e.Handled = true;
        }
    }

    private static void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsShiftHeld())
            return;

        DependencyObject? source = e.OriginalSource as DependencyObject;
        HelpDescriptor? descriptor = FindHelpTarget(source);
        if (descriptor is null)
            return;

        ShowPopup(sender as UIElement, descriptor.Value);

        // Do not mark Handled for TextBox so selection isn't disrupted; ContextMenuOpening handles menu suppression.
        if (source is not TextBoxBase)
            e.Handled = true;
    }

    private static bool IsShiftHeld()
        => (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

    private static void ShowPopup(UIElement? placementTarget, HelpDescriptor descriptor)
    {
        if (_sharedPopup is null || _popupContent is null)
        {
            _popupContent = new ContextualHelpPopup();
            _sharedPopup = new Popup
            {
                Child = _popupContent,
                AllowsTransparency = true,
                StaysOpen = false,
                PopupAnimation = PopupAnimation.Fade,
                Placement = PlacementMode.MousePoint,
                HorizontalOffset = 8,
                VerticalOffset = 8
            };
        }

        _popupContent.SetContent(descriptor.Title, descriptor.Summary, descriptor.Impact);
        _sharedPopup.PlacementTarget = placementTarget;
        _sharedPopup.IsOpen = false;
        _sharedPopup.IsOpen = true;
    }

    private static HelpDescriptor? FindHelpTarget(DependencyObject? start)
    {
        DependencyObject? current = start;
        DependencyObject? fallbackInteractive = null;

        while (current is not null)
        {
            string? explicitTitle = GetTitle(current);
            string? explicitSummary = GetSummary(current);

            if (!string.IsNullOrWhiteSpace(explicitTitle) || !string.IsNullOrWhiteSpace(explicitSummary))
            {
                return new HelpDescriptor(
                    explicitTitle ?? InferControlTitle(current),
                    explicitSummary ?? "Interactive control.",
                    GetImpact(current) ?? "Reversible — does not make permanent changes.");
            }

            if (fallbackInteractive is null && IsInteractiveControl(current))
                fallbackInteractive = current;

            current = GetParent(current);
        }

        if (fallbackInteractive is null)
            return null;

        return new HelpDescriptor(
            InferControlTitle(fallbackInteractive),
            InferControlSummary(fallbackInteractive),
            "Reversible — adjusts view or setting without deleting data.");
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is FrameworkContentElement fce)
            return fce.Parent ?? ContentOperations.GetParent(fce);

        DependencyObject? visualParent = element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : null;

        if (visualParent is not null)
            return visualParent;

        return (element as FrameworkElement)?.Parent;
    }

    private static bool IsInteractiveControl(DependencyObject obj) => obj is
        ButtonBase or
        TextBoxBase or
        Slider or
        Selector or
        TabItem or
        MenuItem or
        ComboBox;

    private static string InferControlTitle(DependencyObject obj)
    {
        string? autoName = AutomationProperties.GetName(obj);
        if (!string.IsNullOrWhiteSpace(autoName))
            return autoName;

        return obj switch
        {
            TabItem tab when tab.Header is string h => $"{h} Tab",
            ContentControl cc when cc.Content is string text && !string.IsNullOrWhiteSpace(text) => text,
            TextBox => "Text Input Field",
            Slider => "Setting Slider",
            CheckBox => "Toggle Option",
            Button => "Action Button",
            DataGrid => "Data Table",
            ListView => "Selectable List",
            _ => obj.GetType().Name
        };
    }

    private static string InferControlSummary(DependencyObject obj)
    {
        if (obj is FrameworkElement fe && fe.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip))
            return tip;

        return obj switch
        {
            TabItem => "Switches to this section of Memory Magic.",
            TextBox => "Type here to filter or update this value.",
            Slider => "Drag left or right to adjust this numeric threshold.",
            CheckBox => "Click to turn this option on or off.",
            Button => "Click to run this action.",
            DataGrid => "Click a row to select an item, or click column headers to sort.",
            ListView => "Review items and check or uncheck the ones you want to include.",
            _ => "Interactive control in Memory Magic."
        };
    }

    private readonly record struct HelpDescriptor(string Title, string Summary, string Impact);
}
