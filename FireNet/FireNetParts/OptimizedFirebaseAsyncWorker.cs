using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class OptimizedFirebaseAsyncWorker : IDisposable
{
    private readonly ConcurrentQueue<WorkItem> operationQueue = new ConcurrentQueue<WorkItem>();
    private readonly CancellationTokenSource internalCancellationSource;
    private readonly CancellationToken combinedCancellationToken;
    private readonly Task workerTask;
    private volatile bool isRunning = false;
    private volatile bool isDisposed = false;

    // Performance tracking
    private int operationsProcessed = 0;
    private int operationsFailed = 0;
    private readonly object statsLock = new object();

    // Adaptive batching
    private const int MIN_BATCH_SIZE = 3;
    private const int MAX_BATCH_SIZE = 10;
    private const int BASE_DELAY_MS = 16; // ~60 FPS
    private const int MAX_DELAY_MS = 100;
    private const int MAX_QUEUE_SIZE = 1000;

    private int currentBatchSize = MIN_BATCH_SIZE;
    private int currentDelayMs = BASE_DELAY_MS;
    private DateTime lastProcessTime = DateTime.UtcNow;

    private struct WorkItem
    {
        public Func<Task> Operation;
        public DateTime EnqueuedAt;
        public int Priority; // 0 = high, 1 = normal, 2 = low
        public string DebugName;
    }

    public OptimizedFirebaseAsyncWorker(CancellationToken externalToken)
    {
        internalCancellationSource = new CancellationTokenSource();
        combinedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
            externalToken,
            internalCancellationSource.Token
        ).Token;

        isRunning = true;
        workerTask = Task.Run(WorkerLoop, combinedCancellationToken);
    }

    public void Stop()
    {
        if (isDisposed || !isRunning) return;

        isRunning = false;

        try
        {
            internalCancellationSource.Cancel();

            // Wait for graceful shutdown with timeout
            if (!workerTask.Wait(2000))
            {
                Debug.LogWarning("AsyncWorker did not shut down gracefully within timeout");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error stopping AsyncWorker: {e}");
        }
    }

    public void EnqueueOperation(Func<Task> operation, int priority = 1, string debugName = "Unknown")
    {
        if (isDisposed || !isRunning || operation == null)
            return;

        // Prevent queue overflow
        if (operationQueue.Count >= MAX_QUEUE_SIZE)
        {
            Debug.LogWarning($"AsyncWorker queue full ({MAX_QUEUE_SIZE}), dropping operation: {debugName}");
            return;
        }

        var workItem = new WorkItem
        {
            Operation = operation,
            EnqueuedAt = DateTime.UtcNow,
            Priority = priority,
            DebugName = debugName
        };

        operationQueue.Enqueue(workItem);
    }

    private async Task WorkerLoop()
    {
        var processedBatch = new List<Task>();
        var batchStartTime = DateTime.UtcNow;

        while (isRunning && !combinedCancellationToken.IsCancellationRequested)
        {
            try
            {
                processedBatch.Clear();
                int processed = 0;
                batchStartTime = DateTime.UtcNow;

                // Process a batch of operations
                while (processed < currentBatchSize && operationQueue.TryDequeue(out var workItem))
                {
                    // Check for stale operations (older than 30 seconds)
                    if (DateTime.UtcNow - workItem.EnqueuedAt > TimeSpan.FromSeconds(30))
                    {
                        Debug.LogWarning($"Dropping stale operation: {workItem.DebugName}");
                        processed++;
                        continue;
                    }

                    var operationTask = ExecuteWithTimeout(workItem);
                    processedBatch.Add(operationTask);
                    processed++;
                }

                // Execute batch if we have operations
                if (processedBatch.Count > 0)
                {
                    await ProcessBatch(processedBatch);

                    lock (statsLock)
                    {
                        operationsProcessed += processedBatch.Count;
                    }

                    // Adaptive batching based on processing time
                    AdaptBatchingStrategy(DateTime.UtcNow - batchStartTime);
                }
                else
                {
                    // No operations to process, increase delay to save CPU
                    currentDelayMs = Math.Min(currentDelayMs + 2, MAX_DELAY_MS);
                }

                // Adaptive delay based on queue pressure
                int delayMs = CalculateAdaptiveDelay();
                await Task.Delay(delayMs, combinedCancellationToken);
            }
            catch (OperationCanceledException)
            {
                Debug.Log("AsyncWorker loop cancelled");
                break;
            }
            catch (Exception e)
            {
                Debug.LogError($"AsyncWorker loop error: {e}");

                // Exponential backoff on errors
                try
                {
                    await Task.Delay(Math.Min(1000, currentDelayMs * 2), combinedCancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Debug.Log("AsyncWorker loop ended");
    }

    private async Task ProcessBatch(List<Task> batch)
    {
        try
        {
            // Wait for all operations in the batch to complete
            await Task.WhenAll(batch);
        }
        catch (Exception e)
        {
            // Log batch failure but continue processing
            Debug.LogWarning($"Batch processing failed: {e.Message}");

            // Count individual failures
            foreach (var task in batch)
            {
                if (task.IsFaulted)
                {
                    lock (statsLock)
                    {
                        operationsFailed++;
                    }
                }
            }
        }
    }

    private async Task ExecuteWithTimeout(WorkItem workItem)
    {
        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) // Increased timeout
        using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            combinedCancellationToken,
            timeoutCts.Token))
        {
            try
            {
                await workItem.Operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (combinedCancellationToken.IsCancellationRequested)
            {
                // Expected cancellation, don't log
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                Debug.LogWarning($"Operation timed out: {workItem.DebugName}");
                lock (statsLock)
                {
                    operationsFailed++;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Operation failed '{workItem.DebugName}': {e.Message}");
                lock (statsLock)
                {
                    operationsFailed++;
                }
            }
        }
    }

    private void AdaptBatchingStrategy(TimeSpan processingTime)
    {
        const double targetProcessingTimeMs = 50; // Target 50ms per batch
        double actualTimeMs = processingTime.TotalMilliseconds;

        if (actualTimeMs > targetProcessingTimeMs * 1.5) // Taking too long
        {
            currentBatchSize = Math.Max(MIN_BATCH_SIZE, currentBatchSize - 1);
            currentDelayMs = Math.Min(currentDelayMs + 5, MAX_DELAY_MS);
        }
        else if (actualTimeMs < targetProcessingTimeMs * 0.5) // Processing too fast
        {
            currentBatchSize = Math.Min(MAX_BATCH_SIZE, currentBatchSize + 1);
            currentDelayMs = Math.Max(BASE_DELAY_MS, currentDelayMs - 2);
        }
    }

    private int CalculateAdaptiveDelay()
    {
        int queueSize = operationQueue.Count;

        if (queueSize > 50) // High pressure
        {
            return BASE_DELAY_MS;
        }
        else if (queueSize > 20) // Medium pressure
        {
            return BASE_DELAY_MS + 5;
        }
        else if (queueSize > 0) // Low pressure
        {
            return BASE_DELAY_MS + 10;
        }
        else // No pressure
        {
            return Math.Min(currentDelayMs, MAX_DELAY_MS);
        }
    }

    public AsyncWorkerStats GetStats()
    {
        lock (statsLock)
        {
            return new AsyncWorkerStats
            {
                QueueSize = operationQueue.Count,
                OperationsProcessed = operationsProcessed,
                OperationsFailed = operationsFailed,
                CurrentBatchSize = currentBatchSize,
                CurrentDelayMs = currentDelayMs,
                IsRunning = isRunning,
                IsDisposed = isDisposed
            };
        }
    }

    public void Dispose()
    {
        if (isDisposed) return;

        isDisposed = true;

        try
        {
            Stop();
            internalCancellationSource?.Dispose();
        }
        catch (Exception e)
        {
            Debug.LogError($"Error disposing AsyncWorker: {e}");
        }
    }
}

public struct AsyncWorkerStats
{
    public int QueueSize;
    public int OperationsProcessed;
    public int OperationsFailed;
    public int CurrentBatchSize;
    public int CurrentDelayMs;
    public bool IsRunning;
    public bool IsDisposed;

    public float SuccessRate => OperationsProcessed > 0 ?
        (float)(OperationsProcessed - OperationsFailed) / OperationsProcessed : 0f;
}