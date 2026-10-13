namespace ChargeGlobe.Jobs.Tests;

public class JobOptionsTests
{
    [Theory]
    [InlineData("crawler", JobRole.Crawler)]
    [InlineData("worker", JobRole.Worker)]
    [InlineData(" Worker ", JobRole.Worker)]
    public void Create_maps_the_role_argument(string role, JobRole expected)
    {
        var options = JobOptions.Create(role, "instance-1");

        Assert.Equal(expected, options.Role);
        Assert.Equal("instance-1", options.InstanceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api")]
    public void Create_rejects_unknown_or_missing_roles(string? role)
    {
        var exception = Assert.Throws<ArgumentException>(() => JobOptions.Create(role, "instance-1"));

        Assert.Contains("--role crawler|worker", exception.Message);
    }
}
