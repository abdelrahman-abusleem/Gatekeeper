using System.Collections.Concurrent;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Domain.UnitTests;

public sealed class AdmissionIdVersion7Tests
{
    [Fact]
    public void New_UsesNativeV7AndRemainsUniqueUnderConcurrentGeneration()
    {
        var ids = new ConcurrentBag<AdmissionId>();
        Parallel.For(0, 5000, _ => ids.Add(AdmissionId.New()));
        Assert.Equal(5000, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            Assert.Equal(7, id.Value.Version);
            Assert.Equal(id.Value, Guid.ParseExact(id.ToString(), "D"));
        });
    }

    [Fact]
    public void ExistingV4_RemainsRepresentable()
    {
        var guid = Guid.NewGuid();
        var id = new AdmissionId(guid);
        Assert.Equal(4, id.Value.Version);
        Assert.Equal(guid.ToString("D"), id.ToString());
    }
}
