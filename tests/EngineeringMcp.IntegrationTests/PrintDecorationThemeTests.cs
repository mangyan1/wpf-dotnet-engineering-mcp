using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed class PrintDecorationThemeTests
{
    [TestMethod]
    public void GearDecoration_IsNotRestrictedToPrintTheme()
    {
        var root = TestRepositoryLocator.FindRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EngineeringMcp.ControlCenter",
            "MainWindow.xaml.cs"));

        Assert.DoesNotContain("if (_activeThemeMode != \"Print\")", source, StringComparison.Ordinal,
            "The gear train is shared dashboard identity and must not be collapsed outside Print mode.");
        StringAssert.Contains(source, "PrintSheet.SetThemeMode",
            "Theme changes must update decoration contrast and print-only furniture without hiding the gears.");
    }
}
