namespace CS2_SimpleAdmin.Tests;

public class Smoke
{
    [Fact]
    public void PluginStaticsCanBeTouchedWithoutTheEngine()
    {
        // Touches the CS2_SimpleAdmin type initializer: it must not construct a BasePlugin (which registers native listeners)
        Assert.Null(CS2_SimpleAdmin.DatabaseProvider);
        TestConfig.Use();
        Assert.Equal(1, CS2_SimpleAdmin.CurrentConfig.OtherSettings.TimeMode);
    }
}
