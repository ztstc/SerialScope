using Microsoft.UI.Xaml;
using Windows.UI;

namespace SerialScope.Models;

/// <summary>Colors used only to distinguish waveform channels.</summary>
public static class ThemePalette
{
    public static Color[] ChannelColors(ElementTheme theme) => theme == ElementTheme.Light
        ? new[]
        {
            Color.FromArgb(255, 14, 116, 144),
            Color.FromArgb(255, 4, 120, 87),
            Color.FromArgb(255, 180, 83, 9),
            Color.FromArgb(255, 190, 18, 60),
            Color.FromArgb(255, 109, 40, 217),
            Color.FromArgb(255, 29, 78, 216),
            Color.FromArgb(255, 194, 65, 12),
            Color.FromArgb(255, 190, 24, 93),
        }
        : new[]
        {
            Color.FromArgb(255, 34, 211, 238),
            Color.FromArgb(255, 52, 211, 153),
            Color.FromArgb(255, 251, 191, 36),
            Color.FromArgb(255, 251, 113, 133),
            Color.FromArgb(255, 167, 139, 250),
            Color.FromArgb(255, 96, 165, 250),
            Color.FromArgb(255, 251, 146, 60),
            Color.FromArgb(255, 244, 114, 182),
        };
}
