namespace Gatekeeper.Domain.Enums;
/// <summary>A current Redis observation, never historical admission or authorization proof.</summary>
public enum AdmissionStatus
{
    /// <summary>No current Waiting, Processing or live Active admission was observed.</summary>
    NotFound,
    /// <summary>The user has a Waiting entry.</summary>
    Waiting,
    /// <summary>A structurally consistent reservation exists; even an expired lease requires Recovery.</summary>
    Processing,
    /// <summary>Structurally live Active state exists; this alone does not authorize admission.</summary>
    Active,
    /// <summary>Admission state is malformed or conflicting and requires investigation.</summary>
    Inconsistent
}
