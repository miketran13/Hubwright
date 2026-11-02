using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// Brand and look: the theme and logo editor and the page mockup, as two tabs of one page. Each tab
/// is built the first time it is shown, so only one preview loads at a time.
/// </summary>
public sealed class BrandPage : Grid
{
    public BrandPage(string tab, string? folder)
    {
        var pivot = new Pivot { Margin = new Thickness(8, 4, 8, 0) };
        var theme = new PivotItem { Header = "Theme and logo", Tag = "theme-editor" };
        var mockup = new PivotItem { Header = "Page mockup", Tag = "preview" };
        pivot.Items.Add(theme);
        pivot.Items.Add(mockup);
        pivot.SelectionChanged += (_, _) =>
        {
            if (pivot.SelectedItem is PivotItem { Content: null } item)
            {
                item.Content = (string)item.Tag == "preview" ? new PreviewPage() : new ThemeEditorPage(folder);
            }
        };
        pivot.SelectedItem = tab == "preview" ? mockup : theme;
        if (pivot.SelectedItem is PivotItem { Content: null } first)
        {
            first.Content = tab == "preview" ? new PreviewPage() : new ThemeEditorPage(folder);
        }

        Children.Add(pivot);
    }
}
