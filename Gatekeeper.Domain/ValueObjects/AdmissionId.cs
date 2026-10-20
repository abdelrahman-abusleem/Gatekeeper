namespace Gatekeeper.Domain.ValueObjects;

public readonly record struct AdmissionId
{
    public Guid Value { get; }


    public AdmissionId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException(
                "AdmissionId cannot be empty.",
                nameof(value));

        Value = value;
    }

    public static AdmissionId New()
        => new(Guid.CreateVersion7());

    public override string ToString()
        => Value.ToString("D");
}
