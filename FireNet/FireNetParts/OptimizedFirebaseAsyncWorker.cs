using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class OptimizedFirebaseAsyncWorker
{
    private readonly ConcurrentQueue<Func<Task>> operationQueue = new ConcurrentQueue<Func<Task>>();
    private readonly CancellationToken cancellationToken;
    private readonly Task workerTask;
    private volatile bool isRunning = false;

    public OptimizedFirebaseAsyncWorker(CancellationToken token)
    {
        cancellationToken = token;
        isRunning = true;

        workerTask = Task.Run(WorkerLoop, token);
    }

    public void Stop()
    {
        isRunning = false;
        workerTask?.Wait(1000);
    }

    public void EnqueueOperation(Func<Task> operation)
    {
        if (isRunning && !cancellationToken.IsCancellationRequested)
        {
            operationQueue.Enqueue(operation);
        }
    }

    private async Task WorkerLoop()
    {

        const int maxBatchSize = 5;
        const int delayMs = 16;

        while (isRunning && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var batch = new List<Task>();
                int processed = 0;

                while (processed < maxBatchSize && operationQueue.TryDequeue(out var operation))
                {
                    batch.Add(ExecuteWithTimeout(operation));
                    processed++;
                }

                if (batch.Count > 0)
                {

                    _ = Task.WhenAll(batch).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            Debug.LogWarning($"Batch operation failed: {t.Exception?.GetBaseException()}");
                        }
                    });
                }

                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogError($"Firebase worker error: {e}");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private async Task ExecuteWithTimeout(Func<Task> operation)
    {
        try
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            using (var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                await operation().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

        }
        catch (Exception e)
        {
            Debug.LogWarning($"Firebase operation failed: {e.Message}");
        }
    }
}