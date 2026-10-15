using ChargeGlobe.Application.Locations;
using ChargeGlobe.Domain.Charging;

namespace ChargeGlobe.Api.Tests;

public class AvailabilityTests
{
    [Fact]
    public void The_status_groups_are_OCPI_statuses_and_decide_the_availability()
    {
        Assert.All(Availability.OccupiedStatuses.Concat(Availability.FaultedStatuses), status => Assert.True(Enum.IsDefined(Enum.Parse<EvseStatus>(status))));
        Assert.Equal(
            [Availability.Available, Availability.Occupied, Availability.OutOfOrder, Availability.Unknown],
            [Availability.Of(1, 1, 1), Availability.Of(0, 1, 1), Availability.Of(0, 0, 1), Availability.Of(0, 0, 0)]);
    }
}
