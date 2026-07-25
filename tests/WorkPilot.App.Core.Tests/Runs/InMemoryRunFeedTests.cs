using System;
using System.Collections.Generic;
using System.Linq;
using WorkPilot.App.Core.Runs;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;
using Xunit;

namespace WorkPilot.App.Core.Tests.Runs;

public class InMemoryRunFeedTests
{
    private static RunEvent MakeEv(string id) =>
        RunEvent.Create(RunEventId.Parse(id), RunId.Parse("r"), "k", RunEventLevel.Info, "C", "C", "{}", "c", DateTimeOffset.UnixEpoch, null, null);

    [Fact]
    public void Publish_delivers_to_subscriber()
    {
        var feed = new InMemoryRunFeed();
        RunFeedItem? got = null;
        using var _ = feed.Subscribe(i => got = i);
        var item = new RunFeedItem(RunId.Parse("r1"), Array.Empty<RunEvent>(), false);
        feed.Publish(item);
        Assert.Equal(item, got);
    }

    [Fact]
    public void Coalescing_merges_events_for_same_run_until_flush()
    {
        var feed = new InMemoryRunFeed();
        var received = new List<RunFeedItem>();
        using var _ = feed.Subscribe(i => received.Add(i));
        var r = RunId.Parse("r1");

        feed.BeginCoalescing();
        feed.Publish(new RunFeedItem(r, new[] { MakeEv("e1") }, false));
        feed.Publish(new RunFeedItem(r, new[] { MakeEv("e2") }, true));
        Assert.Empty(received); // buffered, not yet emitted

        feed.Flush();
        Assert.Single(received);
        Assert.Equal(2, received[0].Events.Count);
        Assert.True(received[0].Terminal); // OR of both publishes
    }

    [Fact]
    public void Coalescing_separates_different_runs()
    {
        var feed = new InMemoryRunFeed();
        var received = new List<RunFeedItem>();
        using var _ = feed.Subscribe(i => received.Add(i));

        feed.BeginCoalescing();
        feed.Publish(new RunFeedItem(RunId.Parse("r1"), new[] { MakeEv("e1") }, false));
        feed.Publish(new RunFeedItem(RunId.Parse("r2"), new[] { MakeEv("e2") }, false));
        feed.Flush();

        Assert.Equal(2, received.Count);
        Assert.Contains(received, i => i.RunId == RunId.Parse("r1"));
        Assert.Contains(received, i => i.RunId == RunId.Parse("r2"));
    }

    [Fact]
    public void Unsubscribe_stops_delivery()
    {
        var feed = new InMemoryRunFeed();
        var count = 0;
        var sub = feed.Subscribe(_ => count++);
        feed.Publish(new RunFeedItem(RunId.Parse("r1"), Array.Empty<RunEvent>(), false));
        sub.Dispose();
        feed.Publish(new RunFeedItem(RunId.Parse("r1"), Array.Empty<RunEvent>(), false));
        Assert.Equal(1, count);
    }
}
