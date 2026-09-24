using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Praxsuite.Tests
{
    /// <summary>
    /// Guards the SDK against the one platform assumption that breaks it silently.
    ///
    /// Unity's WebGL player is single-threaded. <c>Thread</c> does not start, and anything
    /// queued to the thread pool is never picked up, because there are no worker threads to
    /// pick it up. Any await whose continuation is SCHEDULED rather than run therefore never
    /// resumes - and it does not fail, it just stays pending. No exception, no timeout, no log.
    ///
    /// A user hit exactly that on a WebGL build: <c>LoginAsync</c> never returned, while the
    /// browser's network tab showed the SDK's <c>/auth/config</c> discovery call answering
    /// 200. The request worked; the continuation after it never ran.
    ///
    /// These tests run on desktop .NET, where the thread pool does exist, so they cannot
    /// reproduce a WebGL hang directly. What they CAN do is assert the property WebGL needs:
    /// that a continuation runs on the thread that completes the task, with no scheduler
    /// involved. If that holds, a runtime with one thread and no pool still resumes.
    /// </summary>
    [TestFixture]
    public class PraxWebGlTests
    {
        /// <summary>
        /// The core assertion. The SDK's completion sources must resume their awaiter inline,
        /// on the completing thread.
        ///
        /// Written without any thread pool dependency on purpose: the continuation must have
        /// already run by the time TrySetResult returns. With
        /// TaskCreationOptions.RunContinuationsAsynchronously this fails here, and on WebGL it
        /// would hang forever instead.
        /// </summary>
        [Test]
        public void ContinuationRunsInlineOnTheCompletingThread()
        {
            var completion = PraxCompletion.Create<int>();

            var resumed = false;
            var resumedOnThread = 0;
            var completedOnThread = Thread.CurrentThread.ManagedThreadId;

            // ConfigureAwait(false) matches how the SDK awaits internally. That is the flag
            // that sends a scheduled continuation to the thread pool rather than back to a
            // captured context, so the test has to include it to be meaningful.
            async Task Await()
            {
                await completion.Task.ConfigureAwait(false);
                resumed = true;
                resumedOnThread = Thread.CurrentThread.ManagedThreadId;
            }

            var awaiting = Await();
            Assert.IsFalse(resumed, "the continuation must not run before the task completes");

            completion.TrySetResult(1);

            Assert.IsTrue(resumed,
                "The continuation had not run when TrySetResult returned, so it was handed to a " +
                "scheduler. WebGL has no thread pool to run it, so the awaiting call would hang " +
                "there with no error - which is the LoginAsync bug.");
            Assert.AreEqual(completedOnThread, resumedOnThread,
                "the continuation must run on the completing thread, which on WebGL is the only thread");
            Assert.IsTrue(awaiting.IsCompleted, "the awaiting task should be finished");
        }

        /// <summary>A faulted completion has to resume inline too, or an error never surfaces.</summary>
        [Test]
        public void FaultedContinuationAlsoRunsInline()
        {
            var completion = PraxCompletion.Create<int>();

            var observed = false;
            async Task Await()
            {
                try { await completion.Task.ConfigureAwait(false); }
                catch (PraxException) { observed = true; }
            }

            var awaiting = Await();
            completion.TrySetException(new PraxException("BOOM", "test"));

            Assert.IsTrue(observed,
                "a failure must reach the caller inline; scheduled, it would be swallowed on WebGL");
            Assert.IsTrue(awaiting.IsCompleted);
        }

        /// <summary>
        /// Cancellation, same rule. A cancelled request that never resumes its awaiter looks
        /// identical to a hung one from the game's side.
        /// </summary>
        [Test]
        public void CancelledContinuationAlsoRunsInline()
        {
            var completion = PraxCompletion.Create<int>();
            using (var cts = new CancellationTokenSource())
            {
                var observed = false;
                async Task Await()
                {
                    try { await completion.Task.ConfigureAwait(false); }
                    catch (TaskCanceledException) { observed = true; }
                }

                var awaiting = Await();
                cts.Cancel();
                completion.TrySetCanceled(cts.Token);

                Assert.IsTrue(observed, "a cancellation must reach the caller inline");
                Assert.IsTrue(awaiting.IsCompleted);
            }
        }

        /// <summary>
        /// A chain of awaits must stay inline end to end.
        ///
        /// This is the shape the real bug had: LoginAsync awaits SendJsonAsync, which awaits
        /// SendAsync, which awaits credential resolution, which awaits the /auth/config
        /// request. One scheduled hop anywhere in that chain strands the whole call.
        /// </summary>
        [Test]
        public void AChainOfAwaitsStaysInline()
        {
            var completion = PraxCompletion.Create<int>();

            async Task<int> Inner() => await completion.Task.ConfigureAwait(false);
            async Task<int> Middle() => await Inner().ConfigureAwait(false) + 1;

            var resumed = false;
            async Task Outer()
            {
                await Middle().ConfigureAwait(false);
                resumed = true;
            }

            var awaiting = Outer();
            completion.TrySetResult(1);

            Assert.IsTrue(resumed, "every hop in the chain must resume inline, not just the first");
            Assert.IsTrue(awaiting.IsCompleted);
        }
    }
}
