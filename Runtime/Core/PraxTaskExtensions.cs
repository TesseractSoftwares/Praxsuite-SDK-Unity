using System.Collections;
using System.Threading.Tasks;

namespace Praxsuite
{
    /// <summary>
    /// Bridges the SDK's Tasks into coroutines, for projects that have not moved to
    /// async/await.
    ///
    /// <code>
    /// IEnumerator Start()
    /// {
    ///     var login = Prax.Auth.LoginAsync(email, password);
    ///     yield return login.AsCoroutine();
    ///
    ///     if (login.IsFaulted) { ShowError(login.Exception); yield break; }
    ///     Debug.Log("Signed in as " + login.Result.User.DisplayName);
    /// }
    /// </code>
    ///
    /// A coroutine cannot propagate an exception, so a failed task does not throw here -
    /// always check <c>IsFaulted</c> afterwards. If you would rather it threw, use
    /// <see cref="AsThrowingCoroutine"/>, or move the method to async/await where try/catch
    /// works normally.
    /// </summary>
    public static class PraxTaskExtensions
    {
        /// <summary>Yields until the task finishes, successfully or not.</summary>
        public static IEnumerator AsCoroutine(this Task task)
        {
            if (task == null) yield break;
            while (!task.IsCompleted) yield return null;

            // Observe the exception so it does not resurface as an unobserved-task warning
            // from the finalizer thread, where the stack trace is useless.
            if (task.IsFaulted && task.Exception != null)
                PraxLog.Verbose("Awaited task faulted: " + task.Exception.GetBaseException().Message);
        }

        /// <summary>Yields until the task finishes, successfully or not.</summary>
        public static IEnumerator AsCoroutine<T>(this Task<T> task)
        {
            return AsCoroutine((Task)task);
        }

        /// <summary>
        /// Yields until the task finishes, then rethrows any failure on the main thread. The
        /// throw escapes the coroutine and lands in Unity's console, so use it when a failure
        /// should be loud rather than handled.
        /// </summary>
        public static IEnumerator AsThrowingCoroutine(this Task task)
        {
            if (task == null) yield break;
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted && task.Exception != null)
                throw task.Exception.GetBaseException();
        }
    }
}
