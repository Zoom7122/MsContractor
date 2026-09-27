using Confluent.Kafka;
using MsContractor.CatalogSyncService.Consumers;

namespace MsContractor.Sync.Tests;

public sealed class SyncRequestedConsumerTests
{
    [Fact]
    public async Task RetryAtOffsetAsync_SeeksToTheFailedRecordThenWaitsBeforeRetry()
    {
        var offset = new TopicPartitionOffset(new TopicPartition("sync.commands", new Partition(3)), new Offset(41));
        var events = new List<string>();
        TopicPartitionOffset? soughtOffset = null;
        TimeSpan? requestedDelay = null;
        using var cancellation = new CancellationTokenSource();

        var seekSucceeded = await SyncRequestedConsumer.RetryAtOffsetAsync(
            _ => true,
            value =>
            {
                soughtOffset = value;
                events.Add("seek");
            },
            offset,
            cancellation.Token,
            (delay, token) =>
            {
                requestedDelay = delay;
                Assert.Equal(cancellation.Token, token);
                events.Add("delay");
                return Task.CompletedTask;
            });

        Assert.True(seekSucceeded);
        Assert.Equal(offset, soughtOffset);
        Assert.Equal(TimeSpan.FromSeconds(2), requestedDelay);
        Assert.Equal(["seek", "delay"], events);
    }

    [Fact]
    public async Task RetryAtOffsetAsync_DoesNotSeekWhenPartitionIsNoLongerAssigned()
    {
        var offset = new TopicPartitionOffset(new TopicPartition("sync.commands", new Partition(2)), new Offset(10));
        var seekCalled = false;
        var delayCalled = false;

        var seekSucceeded = await SyncRequestedConsumer.RetryAtOffsetAsync(
            _ => false,
            _ => seekCalled = true,
            offset,
            CancellationToken.None,
            (_, _) =>
            {
                delayCalled = true;
                return Task.CompletedTask;
            });

        Assert.False(seekSucceeded);
        Assert.False(seekCalled);
        Assert.True(delayCalled);
    }

    [Fact]
    public async Task RetryAtOffsetAsync_CatchesPartitionRevokedBetweenAssignmentCheckAndSeek()
    {
        var offset = new TopicPartitionOffset(new TopicPartition("sync.commands", new Partition(2)), new Offset(11));
        var delayCalled = false;

        var seekSucceeded = await SyncRequestedConsumer.RetryAtOffsetAsync(
            _ => true,
            _ => throw new KafkaException(new Error(ErrorCode.Local_AssignmentLost)),
            offset,
            CancellationToken.None,
            (_, _) =>
            {
                delayCalled = true;
                return Task.CompletedTask;
            });

        Assert.False(seekSucceeded);
        Assert.True(delayCalled);
    }
}
