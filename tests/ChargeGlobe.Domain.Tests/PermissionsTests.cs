using ChargeGlobe.Domain.Identity;

namespace ChargeGlobe.Domain.Tests;

public class PermissionsTests
{
    [Fact]
    public void Catalog_keys_follow_the_resource_action_pattern_and_are_described()
    {
        Assert.All(Permissions.All, permission =>
        {
            Assert.Matches("^[a-z]+:[a-z]+$", permission.Key);
            Assert.False(string.IsNullOrWhiteSpace(permission.Description));
        });
        Assert.Equal(Permissions.All.Count, Permissions.AllKeys.Distinct().Count());
    }

    [Fact]
    public void Catalog_contains_the_agreed_permissions()
    {
        Assert.Equal(
            [
                "evse:read", "price:read", "route:read", "tariff:write", "conflict:resolve", "provider:manage",
                "user:manage", "role:manage", "apikey:manage", "export:read", "settings:read", "settings:manage",
                "jobs:read", "audit:read", "proposal:write",
            ],
            Permissions.AllKeys);
        Assert.True(Permissions.IsKnown("evse:read"));
        Assert.False(Permissions.IsKnown("evse:delete"));
    }

    [Fact]
    public void Default_roles_bundle_only_known_permissions()
    {
        Assert.Equal(["Admin", "Maintainer", "Analyst", "Agent", "User"], DefaultRoles.Bundles.Keys);
        Assert.All(DefaultRoles.Bundles.Values.SelectMany(bundle => bundle), key => Assert.True(Permissions.IsKnown(key)));
        Assert.All(DefaultRoles.Bundles.Values, bundle => Assert.Equal(bundle.Count, bundle.Distinct().Count()));
    }

    [Fact]
    public void Admin_holds_everything_and_User_the_self_service_bundle()
    {
        Assert.Equal(Permissions.AllKeys, DefaultRoles.Bundles[DefaultRoles.Admin]);
        Assert.Equal(
            ["evse:read", "price:read", "route:read", "apikey:manage"],
            DefaultRoles.Bundles[DefaultRoles.User]);
        Assert.Contains(Permissions.ProposalWrite, DefaultRoles.Bundles[DefaultRoles.Agent]);
        Assert.DoesNotContain(Permissions.TariffWrite, DefaultRoles.Bundles[DefaultRoles.Agent]);
    }
}
