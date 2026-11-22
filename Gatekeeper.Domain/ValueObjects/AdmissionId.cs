namespace Gatekeeper.Domain.ValueObjects;

/// <summary>An admission attempt identity; newly generated values use UUIDv7. The protocol rejects unsupported versions.</summary>
public readonly record struct AdmissionId
{
    /// <summary>The underlying GUID; a default-initialized value is empty and rejected by operations.</summary>
    public Guid Value { get; }


    /// <summary>Wrap a nonempty admission identity.</summary>
    /// <param name="value">The nonempty GUID to represent.</param>
    /// <exception cref="ArgumentException">The GUID is empty.</exception>
    public AdmissionId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException(
                "AdmissionId cannot be empty.",
                nameof(value));

        Value = value;
    }

    /// <summary>Create a native UUIDv7 identity using the host clock.</summary>
    /// <returns>A new UUIDv7 admission attempt identity.</returns>
    public static AdmissionId New()
        => new(Guid.CreateVersion7());

    /// <summary>Format the identity as a canonical lowercase GUID D string.</summary>
    /// <returns>The canonical admission identity text.</returns>
    public override string ToString()
        => Value.ToString("D");
}
