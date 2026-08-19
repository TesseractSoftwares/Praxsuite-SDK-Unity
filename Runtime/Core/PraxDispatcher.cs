using System;
using System.Collections.Generic;
using UnityEngine;

namespace Praxsuite
{
    /// <summary>
    /// Runs work on Unity's main thread.
    ///
    /// UnityWebRequest can only be created and polled from the main thread, but SDK calls
    /// are awaited - so a continuation after any <c>await</c> may already be on a worker
    /// thread by the time the caller issues the next request. Rather than making that the
    /// game developer's problem, every request funnels through here.
    ///
    /// The host object is created on demand, hidden, and marked DontDestroyOnLoad so it
    /// survives scene loads.
    /// </summary>
    [AddComponentMenu("")] // hidden from the Add Component menu
    internal class PraxDispatcher : MonoBehaviour
    {
        private static PraxDispatcher _instance;
        private static int _mainThreadId;
        private static readonly Queue<Action> Pending = new Queue<Action>();
        private static readonly object Gate = new object();

        /// <summary>
        /// Captures the main thread id before any scene script runs, so <see cref="IsMainThread"/>
        /// is correct even for the very first SDK call.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            EnsureInstance();
        }

        internal static bool IsMainThread =>
            System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        private static void EnsureInstance()
        {
            if (_instance != null) return;

            var go = new GameObject("PraxsuiteDispatcher")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _instance = go.AddComponent<PraxDispatcher>();
            DontDestroyOnLoad(go);
        }

        /// <summary>
        /// Queues <paramref name="action"/> for the main thread. Runs it inline when the
        /// caller is already on the main thread, so the common path costs nothing.
        /// </summary>
        internal static void Run(Action action)
        {
            if (action == null) return;

            if (IsMainThread)
            {
                // Create the host now if it does not exist yet. Queueing instead would strand
                // the action: nothing drains the queue until an instance is running.
                EnsureInstance();
                action();
                return;
            }

            lock (Gate) Pending.Enqueue(action);
        }

        /// <summary>
        /// Queues unconditionally, even on the main thread. Used to break a synchronous
        /// completion out of the current call stack.
        /// </summary>
        internal static void Post(Action action)
        {
            if (action == null) return;
            lock (Gate) Pending.Enqueue(action);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            if (_mainThreadId == 0)
                _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        private void Update()
        {
            // Drain into a local list so an action that queues more work does not spin
            // this frame, and so the lock is not held while user code runs.
            List<Action> batch = null;
            lock (Gate)
            {
                if (Pending.Count == 0) return;
                batch = new List<Action>(Pending.Count);
                while (Pending.Count > 0) batch.Add(Pending.Dequeue());
            }

            foreach (var action in batch)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // One bad continuation must not stall the queue for every other request.
                    PraxLog.Error("Unhandled exception on the Praxsuite dispatcher.", ex);
                }
            }
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Starts a coroutine on the persistent dispatcher object. Used by the HTTP layer,
        /// which needs a MonoBehaviour that outlives the calling scene.
        /// </summary>
        internal static void StartRoutine(System.Collections.IEnumerator routine)
        {
            EnsureInstance();
            _instance.StartCoroutine(routine);
        }
    }
}
