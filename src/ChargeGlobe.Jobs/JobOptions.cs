namespace ChargeGlobe.Jobs;

/// <param name="Role">Role this process was started with (<c>--role crawler|worker</c>).</param>
/// <param name="InstanceId">Identifies this replica, e.g. the container host name.</param>
public sealed record JobOptions(JobRole Role, string InstanceId)
{
    public static JobOptions Create(string? role, string instanceId) =>
        role?.Trim().ToLowerInvariant() switch
        {
            "crawler" => new JobOptions(JobRole.Crawler, instanceId),
            "worker" => new JobOptions(JobRole.Worker, instanceId),
            _ => throw new ArgumentException(
                $"Unknown or missing role '{role}'. Start with --role crawler|worker.", nameof(role)),
        };
}
