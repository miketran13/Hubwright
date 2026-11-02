using Hubwright.Auth;
using Hubwright.Preview;
using Hubwright.Storage;
using Hubwright.Theming;

namespace Hubwright.Tests;

public class ConnectionTests
{
    [Theory]
    [InlineData("https://contoso.sharepoint.com", "contoso")]
    [InlineData("https://Contoso.SharePoint.com/", "contoso")]
    [InlineData("https://contoso-admin.sharepoint.com", null)]
    [InlineData("https://contoso-my.sharepoint.com", null)]
    [InlineData("https://contoso.sharepoint.us", null)]
    [InlineData("http://contoso.sharepoint.com", null)]
    [InlineData("https://intranet.contoso.com", null)]
    [InlineData(null, null)]
    public void Tenant_prefix_comes_only_from_a_SharePoint_Online_root(string? url, string? expected)
        => Assert.Equal(expected, TenantDiscovery.PrefixFromUrl(url));

    [Fact]
    public void Sign_in_defaults_to_the_browser_and_survives_a_save()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("hubwright-config").FullName, "hubwright.json");
        Assert.Equal(SignInMethod.Browser, new HubwrightConfig().Tenant.SignIn);
        new HubwrightConfig { Tenant = { ClientId = "00000000-0000-0000-0000-000000000001", SignIn = SignInMethod.DeviceCode } }.Save(path);
        Assert.Contains("\"deviceCode\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(SignInMethod.DeviceCode, HubwrightConfig.Load(path).Tenant.SignIn);
    }

    [Fact]
    public void Auth_needs_an_app_but_not_a_tenant_name_until_SharePoint_is_asked_for()
    {
        Assert.Throws<InvalidOperationException>(() => new TenantAuth(new TenantConnection()));
        var auth = new TenantAuth(new TenantConnection { ClientId = "00000000-0000-0000-0000-000000000001" });
        Assert.Equal(TenantAuth.GraphScopes, auth.ScopesFor(TenantResource.Graph));
        Assert.Throws<InvalidOperationException>(() => auth.ScopesFor(TenantResource.SharePoint));
    }

    [Fact]
    public void One_app_reads_with_read_scopes_and_asks_for_write_scopes_only_to_implement()
    {
        var tenant = new TenantConnection { Name = "contoso", ClientId = "00000000-0000-0000-0000-000000000001" };
        var read = new TenantAuth(tenant);
        var write = new TenantAuth(tenant, profile: AccessProfile.Implement);
        Assert.DoesNotContain(read.ScopesFor(TenantResource.Graph).Concat(read.ScopesFor(TenantResource.SharePoint)), s => s.Contains("Write", StringComparison.Ordinal) || s.Contains("FullControl", StringComparison.Ordinal));
        Assert.Equal(["https://contoso.sharepoint.com/AllSites.FullControl"], write.ScopesFor(TenantResource.SharePoint));
        Assert.Contains("https://graph.microsoft.com/SharePointTenantSettings.ReadWrite.All", write.ScopesFor(TenantResource.Graph));
    }

    [Fact]
    public void A_mockup_keeps_its_page_in_another_theme()
    {
        var blueprint = Blueprints.BlueprintGenerator.Generate(new Blueprints.IntranetAnswers());
        var model = MockupModel.FromBlueprint(blueprint);
        var dark = ThemeGenerator.Generate(new ThemeInput("Dark", Rgb.Parse("#6b4fd8")) { Background = Rgb.Parse("#1b1a19") });
        var swapped = model.WithTheme(dark);
        Assert.Same(dark, swapped.Theme);
        Assert.Equal(model.Hubs, swapped.Hubs);
        Assert.Equal(model.HeaderLayout, swapped.HeaderLayout);
    }
}
