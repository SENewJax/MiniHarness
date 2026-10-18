using System;
using System.Threading;
using MiniHarness.Abstractions;

namespace MiniHarness.Server
{
    public sealed class RequestExecution : IDisposable
    {
        private readonly CancellationTokenSource deadline;
        public CancellationToken Token => deadline.Token;
        public TaskExecutionOptions Options { get; }
        public RequestExecution(ServerOptions options, CancellationToken requestAborted)
        {
            deadline = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            Options = new TaskExecutionOptions { CancelTaskWhenWaitCancelled = options.CancelOnDisconnect };
        }
        public void Dispose() => deadline.Dispose();
    }
}
