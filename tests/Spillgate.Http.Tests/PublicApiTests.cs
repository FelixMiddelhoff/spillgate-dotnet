using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Http.Tests;

public sealed class PublicApiTests
{
    [Fact]
    [Covers("SG-R-API-01")]
    public void ThePublicApiIsAsApproved()
    {
        Assert.Equal(string.Empty, ApiDump.Approve("Spillgate.Http", typeof(SpillgateHandler).Assembly));
    }
}
