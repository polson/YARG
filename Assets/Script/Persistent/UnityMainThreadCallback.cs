using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Logging;

namespace YARG
{
    public class UnityMainThreadCallback : MonoBehaviour
    {
        private static readonly Queue<Action> CallbackQueue = new();
        private static readonly List<Action> CallbackBuffer = new();

        private void Update()
        {
            lock (CallbackQueue)
            {
                while (CallbackQueue.Count > 0)
                {
                    CallbackBuffer.Add(CallbackQueue.Dequeue());
                }
            }

            foreach (var callback in CallbackBuffer)
            {
                try
                {
                    callback.Invoke();
                }
                catch (Exception e)
                {
                    YargLogger.LogException(e, "Failed to run main thread callbacks");
                }
            }

            CallbackBuffer.Clear();
        }

        public static void QueueEvent(Action action)
        {
            lock (CallbackQueue)
            {
                CallbackQueue.Enqueue(action);
            }
        }
    }
}