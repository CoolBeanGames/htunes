using System.Windows;

namespace HTunes.App;

// Attached flag the nav-tab template watches: while true, soft blue circles pulse outward
// behind the tab label to show that section is working (tags writing, podcasts downloading,
// iPod syncing).
internal static class TabPulse
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(TabPulse), new PropertyMetadata(false));

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
}
