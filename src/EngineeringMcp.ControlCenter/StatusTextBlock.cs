using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EngineeringMcp.ControlCenter;

/// <summary>
/// Renders "● status" strings (set as plain text by code-behind) with the dot colored
/// by keyword: green for running/pass states, red for failures, neutral otherwise.
/// </summary>
public sealed class StatusTextBlock : TextBlock
{
    private static readonly Brush SuccessBrush = Freeze(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush FailureBrush = Freeze(Color.FromRgb(0xF8, 0x71, 0x71));

    private static readonly string[] SuccessKeywords =
        ["Running", "PASS", "Ready", "Connected", "Guardrails", "repaired"];

    private static readonly string[] FailureKeywords =
        ["Failed", "FAILED", "Missing", "Unavailable", "not found", "Not connected"];

    // ponytail: rebuilding the inlines writes back to TextProperty, so guard against recursion.
    private bool _applying;

    public StatusTextBlock()
    {
        // TextBlock seals OnPropertyChanged, so watch Text through the descriptor instead.
        DependencyPropertyDescriptor.FromProperty(TextProperty, typeof(TextBlock))
            .AddValueChanged(this, (_, _) =>
            {
                if (!_applying)
                    ApplyStatus();
            });
    }

    private void ApplyStatus()
    {
        _applying = true;
        try
        {
            ApplyStatusCore();
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyStatusCore()
    {
        var text = Text ?? string.Empty;
        Inlines.Clear();
        if (!text.StartsWith("●", StringComparison.Ordinal))
        {
            Inlines.Add(text);
            return;
        }

        var status = text["● ".Length..];
        Inlines.Add(new Run("● ") { Foreground = BrushFor(status), FontWeight = FontWeights.Bold });
        Inlines.Add(new Run(status) { FontWeight = FontWeights.SemiBold });
    }

    private static Brush BrushFor(string status)
    {
        foreach (var keyword in FailureKeywords)
            if (status.Contains(keyword, StringComparison.Ordinal))
                return FailureBrush;
        foreach (var keyword in SuccessKeywords)
            if (status.Contains(keyword, StringComparison.Ordinal))
                return SuccessBrush;
        return (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}