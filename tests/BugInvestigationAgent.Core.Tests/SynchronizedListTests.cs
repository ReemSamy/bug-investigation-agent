using BugInvestigationAgent.Core.Context;

namespace BugInvestigationAgent.Core.Tests;

public class SynchronizedListTests
{
    [Test]
    public void Linq_over_a_list_that_grows_concurrently_never_throws()
    {
        // Regression: Home.razor rendered StatusLog.Reverse().Take(80).Reverse() while the pipeline was logging;
        // LINQ read Count, then CopyTo found more items → "Destination array was not long enough".
        var list = new SynchronizedList<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
                list.Add("line " + i++);
        });

        var reads = 0;
        while (!stop.IsCancellationRequested)
        {
            Assert.DoesNotThrow(() => _ = list.Reverse().Take(80).Reverse().ToList());
            Assert.DoesNotThrow(() => _ = list.ToArray());
            Assert.DoesNotThrow(() => _ = list.SafeSnapshot().TakeLast(80).ToList());
            reads++;
        }
        writer.Wait();
        Assert.That(reads, Is.GreaterThan(0));
    }

    [Test]
    public void CopyTo_copies_only_what_fits_and_snapshot_is_consistent()
    {
        var list = new SynchronizedList<int> { 1, 2, 3, 4 };
        var small = new int[2];
        list.CopyTo(small, 0);
        Assert.That(small, Is.EqualTo(new[] { 1, 2 }));

        var snapshot = list.SafeSnapshot();
        list.Add(5);
        Assert.That(snapshot, Is.EqualTo(new[] { 1, 2, 3, 4 }), "snapshot is detached from later writes");
    }
}
