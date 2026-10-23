// Temporary: proves that a failed test shows as a check-run annotation. Removed in the next commit.
public class AnnotationProbeTests
{
    [Fact]
    public void FailsOnPurpose()
    {
        Assert.Equal(1, 2);
    }
}
