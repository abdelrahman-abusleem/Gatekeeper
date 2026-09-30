using FluentAssertions;
using Gatekeeper.Domain.Models;
using Xunit;

namespace Gatekeeper.Domain.UnitTests;

public class QueuePositionTests
{
    [Theory]
    [InlineData(1, 0)]    
    [InlineData(50, 0)]   
    [InlineData(51, 1)]   
    [InlineData(120, 2)]  
    public void Calculate_WithValidInputs_ShouldCalculateEstimatedWaitTimeCorrectly(long position, int expectedMinutes)
    {
        var userId = "user_123";
        var batchSize = 50;
        var interval = TimeSpan.FromMinutes(1);

        var result = QueuePosition.Create(userId, position, batchSize, interval);

        result.UserId.Should().Be(userId);
        result.Position.Should().Be(position);
        result.EstimatedWaitTime.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Calculate_WithNullOrWhitespaceUserId_ShouldThrowException(string? invalidUserId)
    {
        var act = () => QueuePosition.Create(invalidUserId!, 10, 50, TimeSpan.FromMinutes(1));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_WithZeroOrNegativePosition_ShouldThrowException(long invalidPosition)
    {
        var act = () => QueuePosition.Create("user_1", invalidPosition, 50, TimeSpan.FromMinutes(1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Calculate_WithInvalidBatchSize_ShouldThrowException(int invalidBatchSize)
    {
        var act = () => QueuePosition.Create("user_1", 10, invalidBatchSize, TimeSpan.FromMinutes(1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Calculate_WithZeroOrNegativeInterval_ShouldThrowException()
    {
        var actZero = () => QueuePosition.Create("user_1", 10, 50, TimeSpan.Zero);
        actZero.Should().Throw<ArgumentOutOfRangeException>();

        var actNegative = () => QueuePosition.Create("user_1", 10, 50, TimeSpan.FromMinutes(-1));
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }
}