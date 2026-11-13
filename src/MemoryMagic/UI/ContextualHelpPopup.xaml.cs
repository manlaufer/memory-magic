using System.Windows.Controls;
using System.Windows.Media;

namespace MemoryMagic.UI;

public partial class ContextualHelpPopup : UserControl
{
    private static readonly Brush SafeBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));
    private static readonly Brush CautionBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    public ContextualHelpPopup()
    {
        InitializeComponent();
    }

    public void SetContent(string title, string summary, string impact)
    {
        TitleText.Text = title;
        SummaryText.Text = summary;
        ImpactText.Text = $"Side effects: {impact}";

        if (impact.Contains("Permanent", StringComparison.OrdinalIgnoreCase) ||
            impact.Contains("Irreversible", StringComparison.OrdinalIgnoreCase))
        {
            ImpactText.Foreground = DangerBrush;
        }
        else if (impact.Contains("Admin", StringComparison.OrdinalIgnoreCase) ||
                 impact.Contains("Changes", StringComparison.OrdinalIgnoreCase) ||
                 impact.Contains("Restarts", StringComparison.OrdinalIgnoreCase))
        {
            ImpactText.Foreground = CautionBrush;
        }
        else
        {
            ImpactText.Foreground = SafeBrush;
        }
    }
}
