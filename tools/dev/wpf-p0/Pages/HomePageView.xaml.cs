using System;
using System.Windows.Controls;

namespace L_Mechrevo.WpfP0.Pages;

public partial class HomePageView : UserControl
{
    public HomePageView()
    {
        InitializeComponent();
        HomePage.SizeChanged += (_, _) => UpdateHomeContentHeight();
        Loaded += (_, _) => UpdateHomeContentHeight();
    }

    private void UpdateHomeContentHeight()
    {
        double viewport = HomePage.ViewportHeight;
        if (viewport > 0 && Math.Abs(HomeContentRoot.MinHeight - viewport) > 0.5)
        {
            HomeContentRoot.MinHeight = viewport;
        }
    }
}
