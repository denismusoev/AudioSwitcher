using System.Windows;
using System.Windows.Controls;
using AudioSwitcher.Controls;

internal static class ScrollBehaviorChecks
{
    public static int Run()
    {
        const string name = "MakeVisible_NextRowBelowViewport_ScrollsOneRow";

        try
        {
            var panel = new DeviceRowsPanel();
            for (int i = 0; i < 7; i++)
                panel.Children.Add(new Border { Height = 100 });

            panel.Measure(new Size(300, 400));
            panel.Arrange(new Rect(0, 0, 300, 400));

            panel.MakeVisible(panel.Children[4], new Rect(0, 0, 300, 100));

            Require(Math.Abs(panel.VerticalOffset - 100) < 0.01,
                $"Expected one-row offset 100, actual {panel.VerticalOffset:F2}");
            Console.WriteLine("PASS " + name);
            Console.WriteLine("Passed: 1, Failed: 0, Skipped: 0");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + name + ": " + error.Message);
            Console.WriteLine("Passed: 0, Failed: 1, Skipped: 0");
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
