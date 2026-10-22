namespace ForgeOpsTracker.Tests;

public class HistogramBucketerTests
{
    [Fact]
    public void ReturnsTheSmallestBoundaryADurationFitsUnderAsAString()
    {
        Assert.Equal("50", HistogramBucketer.BucketFor(10));
        Assert.Equal("50", HistogramBucketer.BucketFor(50));
        Assert.Equal("100", HistogramBucketer.BucketFor(50.5));
        Assert.Equal("5000", HistogramBucketer.BucketFor(4999));
    }

    [Fact]
    public void ReturnsInfForAnythingLargerThanTheLargestBoundary()
    {
        Assert.Equal("inf", HistogramBucketer.BucketFor(10_001));
        Assert.Equal("inf", HistogramBucketer.BucketFor(1_000_000));
    }

    [Fact]
    public void PutsADurationExactlyOnABoundaryIntoThatBoundarysOwnBucket()
    {
        foreach (var boundary in HistogramBucketer.BoundariesMs)
        {
            Assert.Equal(boundary.ToString(), HistogramBucketer.BucketFor(boundary));
        }
    }

    [Fact]
    public void BoundariesMatchTheServersHistogramPercentile()
    {
        // app/services/histogram_percentile.rb and every other SDK must agree on this exact list.
        Assert.Equal(new long[] { 50, 100, 250, 500, 1000, 2500, 5000, 10_000 }, HistogramBucketer.BoundariesMs);
    }
}
