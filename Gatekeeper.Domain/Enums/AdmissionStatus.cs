namespace Gatekeeper.Domain.Enums;

/// <summary>A current Redis observation, not historical admission or authorization proof.</summary>
public enum AdmissionStatus
{
    NotFound,
    Waiting,
    Processing,
    Active,
    Inconsistent
}
