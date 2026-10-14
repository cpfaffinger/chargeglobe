namespace ChargeGlobe.Domain.Charging;

// The members are named exactly like the OCPI 2.2.1 values (docs/03-datenmodell.md, principle 4),
// so that the text columns, the API and sources with an OCPI export use the same strings.
#pragma warning disable CA1707 // Identifiers should not contain underscores

/// <summary>OCPI <c>Status</c> of an EVSE.</summary>
public enum EvseStatus
{
    UNKNOWN,
    AVAILABLE,
    BLOCKED,
    CHARGING,
    INOPERATIVE,
    OUTOFORDER,
    PLANNED,
    REMOVED,
    RESERVED,
}

/// <summary>OCPI <c>ConnectorType</c>; <see cref="UNKNOWN"/> is our addition for types a source does not name.</summary>
public enum ConnectorStandard
{
    UNKNOWN,
    CHADEMO,
    CHAOJI,
    DOMESTIC_A,
    DOMESTIC_B,
    DOMESTIC_C,
    DOMESTIC_D,
    DOMESTIC_E,
    DOMESTIC_F,
    DOMESTIC_G,
    DOMESTIC_H,
    DOMESTIC_I,
    DOMESTIC_J,
    DOMESTIC_K,
    DOMESTIC_L,
    DOMESTIC_M,
    DOMESTIC_N,
    DOMESTIC_O,
    GBT_AC,
    GBT_DC,
    IEC_60309_2_single_16,
    IEC_60309_2_three_16,
    IEC_60309_2_three_32,
    IEC_60309_2_three_64,
    IEC_62196_T1,
    IEC_62196_T1_COMBO,
    IEC_62196_T2,
    IEC_62196_T2_COMBO,
    IEC_62196_T3A,
    IEC_62196_T3C,
    NEMA_5_20,
    NEMA_6_30,
    NEMA_6_50,
    NEMA_10_30,
    NEMA_10_50,
    NEMA_14_30,
    NEMA_14_50,
    PANTOGRAPH_BOTTOM_UP,
    PANTOGRAPH_TOP_DOWN,
    TESLA_R,
    TESLA_S,
}

/// <summary>OCPI <c>ConnectorFormat</c>: socket or tethered cable.</summary>
public enum ConnectorFormat
{
    SOCKET,
    CABLE,
}

/// <summary>OCPI <c>PowerType</c> of a connector, and the current type of an EVSE.</summary>
public enum PowerType
{
    AC_1_PHASE,
    AC_2_PHASE,
    AC_2_PHASE_SPLIT,
    AC_3_PHASE,
    DC,
}

/// <summary>Who may use a location.</summary>
public enum AccessType
{
    PUBLIC,
    RESTRICTED,
}

/// <summary>Life cycle of a location or an EVSE.</summary>
public enum Lifecycle
{
    ACTIVE,
    PLANNED,
    REMOVED,
}

/// <summary>Whether an EVSE carries a real EVSE-ID or a surrogate assigned by chargeglobe.</summary>
public enum IdentityKind
{
    EVSE_ID,
    SURROGATE,
}
#pragma warning restore CA1707
