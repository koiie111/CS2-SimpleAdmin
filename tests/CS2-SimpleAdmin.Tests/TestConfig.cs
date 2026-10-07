namespace CS2_SimpleAdmin.Tests;

internal static class TestConfig
{
    public static CS2_SimpleAdminConfig Use(Action<CS2_SimpleAdminConfig>? configure = null)
    {
        var config = new CS2_SimpleAdminConfig();
        config.DatabaseConfig.DatabaseType = "MySQL";
        config.Timezone = "Europe/Moscow";
        configure?.Invoke(config);
        CS2_SimpleAdmin.UseConfigWithoutPlugin(config);
        return config;
    }
}
