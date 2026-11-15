using DevOpsAgent.Web.Components.Layout;

namespace DevOpsAgent.Tests;

/// <summary>T8.3: the icon-only theme toggle names the action it performs.</summary>
public sealed class ThemeToggleTests
{
    [Theory]
    [InlineData("Light", "Switch to dark theme")]
    [InlineData("Dark", "Switch to light theme")]
    public void Label_DescribesTheSwitch(string current, string label) => Assert.Equal(label, TopBar.ToggleLabel(current));
}
