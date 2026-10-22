#if HAS_RPC_BUFFER
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharedMemory;

namespace SharedMemoryTests
{
    /// <summary>
    /// TimeoutOrCancel, which every RpcBuffer request waits through: a timeout or a cancellation gives an
    /// unsuccessful response, not an exception. It needs no shared memory, so these run on every OS.
    /// </summary>
    [TestClass]
    public class ResponseTaskHelperTests
    {
        [TestMethod]
        public void TimeoutOrCancel_TimeoutElapses_ReturnsUnsuccessfulResponse()
        {
            var pending = new TaskCompletionSource<RpcResponse>();

            RpcResponse response = pending.Task.TimeoutOrCancel(50).Result;

            Assert.IsFalse(response.Success);
            Assert.IsNull(response.Data);
        }

        [TestMethod]
        public void TimeoutOrCancel_CancelledWithInfiniteTimeout_ReturnsUnsuccessfulResponse()
        {
            var pending = new TaskCompletionSource<RpcResponse>();
            using (var cancellation = new CancellationTokenSource(50))
            {
                RpcResponse response = pending.Task.TimeoutOrCancel(Timeout.Infinite, cancellation.Token).Result;

                Assert.IsFalse(response.Success);
                Assert.IsNull(response.Data);
            }
        }

        [TestMethod]
        public void TimeoutOrCancel_CancelledBeforeTimeout_ReturnsUnsuccessfulResponse()
        {
            var pending = new TaskCompletionSource<RpcResponse>();
            using (var cancellation = new CancellationTokenSource(50))
            {
                RpcResponse response = pending.Task.TimeoutOrCancel(30000, cancellation.Token).Result;

                Assert.IsFalse(response.Success);
                Assert.IsNull(response.Data);
            }
        }

        [TestMethod]
        public void TimeoutOrCancel_CompletesBeforeTimeout_ReturnsTheResponse()
        {
            var pending = new TaskCompletionSource<RpcResponse>();
            var expected = new RpcResponse(true, new byte[] { 1, 2 });

            Task<RpcResponse> waiting = pending.Task.TimeoutOrCancel(30000);
            pending.SetResult(expected);

            Assert.AreSame(expected, waiting.Result);
        }
    }
}
#endif
