using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.PersistentPtys;

public sealed class PersistentPtyInputFrameTests
{
    [Test]
    [Arguments(0, 1, "cols")]
    [Arguments(65536, 1, "cols")]
    [Arguments(1, 0, "rows")]
    [Arguments(1, 65536, "rows")]
    public async Task Encode_Should_Validate_The_Viewport_Before_Renting(int cols, int rows, string parameter)
    {
        var pool = new TrackingByteArrayPool();
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PersistentPtyInputFrame.Encode(1, cols, rows, [], pool));
        await Assert.That(failure.ParamName).IsEqualTo(parameter);
        await Assert.That(pool.RentCount).IsEqualTo(0);
        await Assert.That(pool.ReturnCount).IsEqualTo(0);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }
}
