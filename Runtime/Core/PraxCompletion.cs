using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// Creates the <see cref="TaskCompletionSource{T}"/> the SDK uses to turn a Unity
    /// coroutine into an awaitable Task.
    ///
    /// This exists for one reason: WEBGL HAS NO THREAD POOL.
    ///
    /// The obvious way to write these is with
    /// <c>TaskCreationOptions.RunContinuationsAsynchronously</c>, which is normally good
    /// practice - it stops a caller's continuation running inside whatever code completed the
    /// task, so a long continuation cannot stall the completer. On every platform with real
    /// threads that is exactly right.
    ///
    /// On WebGL it is fatal. Unity's WebGL player is single-threaded: <c>Thread</c> does not
    /// start, and work queued to the thread pool is never picked up because there are no
    /// worker threads to pick it up. RunContinuationsAsynchronously means "do NOT run the
    /// continuation here - schedule it", and combined with the SDK's <c>ConfigureAwait(false)</c>
    /// that schedules it onto <c>TaskScheduler.Default</c>, the thread pool. So the request
    /// completes, the browser shows a perfectly good 200, and the awaiting code never resumes.
    /// No exception, no timeout, no log line - the Task simply stays pending forever.
    ///
    /// That is not hypothetical. It is what a WebGL build did on <c>LoginAsync</c>: the
    /// <c>/auth/config</c> discovery call returned 200 in the network tab and the login Task
    /// sat pending past 170 seconds.
    ///
    /// So the SDK completes inline instead. Every one of these is completed from a coroutine
    /// or from <see cref="PraxDispatcher"/>, both of which already run on Unity's main thread,
    /// so the continuation lands on the main thread either way - which is where a Unity
    /// developer wants it, because the next thing they do is usually touch a GameObject.
    /// </summary>
    internal static class PraxCompletion
    {
        /// <summary>
        /// A completion source whose continuations run on the thread that completes it.
        ///
        /// Complete it from the main thread - a coroutine or <see cref="PraxDispatcher"/> -
        /// and never from a background thread, or the caller's continuation lands there too.
        /// </summary>
        internal static TaskCompletionSource<T> Create<T>()
        {
            // No TaskCreationOptions. The default is inline continuations, which is the only
            // behaviour that works on a runtime without a thread pool.
            return new TaskCompletionSource<T>();
        }
    }
}
