using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cysharp.Threading.Tasks.Linq;

namespace YARG.Helpers
{
    public class Timer
    {
        private CancellationTokenSource _cancellationTokenSource;

        public void Start(float intervalSeconds, Action onTick)
        {
            Stop();
            _cancellationTokenSource = new CancellationTokenSource();

            UniTaskAsyncEnumerable.Timer(
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(intervalSeconds)
                )
                .ForEachAsync(_ => onTick?.Invoke(), _cancellationTokenSource.Token)
                .Forget();
        }

        public void Stop()
        {
            if (_cancellationTokenSource == null)
            {
                return;
            }

            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }
}