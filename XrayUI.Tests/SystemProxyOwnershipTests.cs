using XrayUI.Services;

namespace XrayUI.Tests
{
    public class SystemProxyOwnershipTests
    {
        private const string XrayProxy = "127.0.0.1:16890";
        private const string OtherProxy = "127.0.0.1:7890";

        [Fact]
        public void ClearWithoutSettingProxy_LeavesExistingProxyUntouched()
        {
            var ownership = new SystemProxyOwnership();
            string? currentProxy = OtherProxy;

            ownership.ClearIfOwned(currentProxy, () => currentProxy = null);

            Assert.Equal(OtherProxy, currentProxy);
            Assert.False(ownership.HasProxy);
        }

        [Fact]
        public void StopThenExit_ClearsOwnedProxyOnlyOnce()
        {
            var ownership = new SystemProxyOwnership();
            string? currentProxy = XrayProxy;
            var clearCount = 0;
            ownership.Record(XrayProxy);
            void Clear()
            {
                currentProxy = null;
                clearCount++;
            }

            ownership.ClearIfOwned(currentProxy, Clear);
            ownership.ClearIfOwned(currentProxy, Clear);

            Assert.Null(currentProxy);
            Assert.Equal(1, clearCount);
            Assert.False(ownership.HasProxy);
        }

        [Fact]
        public void AnotherClientTakesOver_LeavesItsProxyAndDropsOwnership()
        {
            var ownership = new SystemProxyOwnership();
            string? currentProxy = OtherProxy;
            ownership.Record(XrayProxy);

            ownership.ClearIfOwned(currentProxy, () => currentProxy = null);

            Assert.Equal(OtherProxy, currentProxy);
            Assert.False(ownership.HasProxy);

            // A later matching address alone must not make this process its owner again.
            currentProxy = XrayProxy;
            ownership.ClearIfOwned(currentProxy, () => currentProxy = null);
            Assert.Equal(XrayProxy, currentProxy);
        }

        [Fact]
        public void ClearFails_ExitCanRetry()
        {
            var ownership = new SystemProxyOwnership();
            string? currentProxy = XrayProxy;
            ownership.Record(XrayProxy);

            Assert.Throws<InvalidOperationException>(() =>
                ownership.ClearIfOwned(currentProxy, () => throw new InvalidOperationException()));

            Assert.True(ownership.HasProxy);
            ownership.ClearIfOwned(currentProxy, () => currentProxy = null);
            Assert.Null(currentProxy);
            Assert.False(ownership.HasProxy);
        }

        [Fact]
        public void SetProxyAgain_CleansUpTheNewPort()
        {
            var ownership = new SystemProxyOwnership();
            string? currentProxy = "127.0.0.1:16891";
            ownership.Record(XrayProxy);
            ownership.Record(currentProxy);

            ownership.ClearIfOwned(currentProxy, () => currentProxy = null);

            Assert.Null(currentProxy);
            Assert.False(ownership.HasProxy);
        }
    }
}
